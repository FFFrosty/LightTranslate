using System.Diagnostics;

namespace CherryTranslate.App;

internal sealed class SelectionController : IDisposable
{
    private readonly SynchronizationContext _uiContext;
    private readonly GlobalMouseHook _mouseHook;
    private readonly SelectionQueryService _queryService;
    private readonly FloatingTranslateButton _button;
    private readonly System.Windows.Forms.Timer _foregroundTimer;
    private Point _downPoint;
    private IntPtr _downWindow;
    private long _downTimestamp;
    private Point _lastClickPoint;
    private IntPtr _lastClickWindow;
    private long _lastClickTimestamp;
    private CancellationTokenSource? _probeCancellation;
    private SelectionProbe? _activeProbe;
    private bool _dragging;
    private bool _paused;
    private bool _disposed;

    public SelectionController(
        SynchronizationContext uiContext,
        GlobalMouseHook mouseHook,
        SelectionQueryService queryService,
        FloatingTranslateButton button)
    {
        _uiContext = uiContext;
        _mouseHook = mouseHook;
        _queryService = queryService;
        _button = button;

        _mouseHook.MouseDown += OnHookMouseDown;
        _mouseHook.MouseUp += OnHookMouseUp;
        _mouseHook.MouseDoubleClick += OnHookDoubleClick;
        _button.Clicked += OnButtonClicked;

        _foregroundTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _foregroundTimer.Tick += (_, _) => HideIfForegroundChanged();
        _foregroundTimer.Start();
    }

    public event Action<SelectionProbe>? TranslateRequested;

    public bool IsPaused => _paused;

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
        {
            CancelProbeAndHide();
        }
    }

    private void OnHookMouseDown(MouseEventInfo info)
        => _uiContext.Post(_ => HandleMouseDown(info), null);

    private void OnHookMouseUp(MouseEventInfo info)
        => _uiContext.Post(_ => HandleMouseUp(info), null);

    private void OnHookDoubleClick(MouseEventInfo info)
        => _uiContext.Post(_ => HandleDoubleClick(info), null);

    private void HandleMouseDown(MouseEventInfo info)
    {
        if (_button.IsPointInside(info.ScreenPoint))
        {
            DemoTrace.Write("controller_down", "inside_button");
            return;
        }

        if (_button.Visible)
        {
            _button.Hide();
            _activeProbe = null;
        }

        _probeCancellation?.Cancel();
        _downPoint = info.ScreenPoint;
        _downWindow = info.ForegroundWindow;
        _downTimestamp = info.Timestamp;
        _dragging = true;
        DemoTrace.Write("controller_down", $"fg=0x{_downWindow.ToInt64():X}");
    }

    private void HandleMouseUp(MouseEventInfo info)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (_paused || _downWindow == IntPtr.Zero || _downWindow != info.ForegroundWindow)
        {
            DemoTrace.Write("controller_up", $"ignored=1;paused={_paused};down=0x{_downWindow.ToInt64():X};up=0x{info.ForegroundWindow.ToInt64():X}");
            return;
        }

        var movement = DistanceSquared(_downPoint, info.ScreenPoint);
        var duration = Stopwatch.GetElapsedTime(_downTimestamp);
        var isDrag = movement >= 16;

        var isDoubleClick = _lastClickWindow == _downWindow &&
                            Stopwatch.GetElapsedTime(_lastClickTimestamp) <= TimeSpan.FromMilliseconds(520) &&
                            DistanceSquared(_lastClickPoint, info.ScreenPoint) <= 64;

        _lastClickPoint = info.ScreenPoint;
        _lastClickWindow = info.ForegroundWindow;
        _lastClickTimestamp = info.Timestamp;

        if (!isDrag && !isDoubleClick)
        {
            DemoTrace.Write("controller_up", $"ignored=1;movement={movement};double={isDoubleClick}");
            return;
        }

        // A very long hold without movement usually belongs to a window/control drag.
        if (!isDrag && duration > TimeSpan.FromSeconds(2))
        {
            DemoTrace.Write("controller_up", "ignored=long_click");
            return;
        }

        DemoTrace.Write("controller_up", $"queue=1;movement={movement};double={isDoubleClick}");
        QueueProbe(_downWindow, info.ScreenPoint);
    }

    private void HandleDoubleClick(MouseEventInfo info)
    {
        if (_paused || _button.IsPointInside(info.ScreenPoint))
        {
            return;
        }

        if (info.ForegroundWindow != IntPtr.Zero)
        {
            QueueProbe(info.ForegroundWindow, info.ScreenPoint);
        }
    }

    private void QueueProbe(IntPtr targetWindow, Point screenPoint)
    {
        _probeCancellation?.Cancel();
        _probeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _probeCancellation = cancellation;
        DemoTrace.Write("probe_start", $"target=0x{targetWindow.ToInt64():X};x={screenPoint.X};y={screenPoint.Y}");
        _ = ProbeAsync(targetWindow, screenPoint, cancellation);
    }

    private async Task ProbeAsync(IntPtr targetWindow, Point screenPoint, CancellationTokenSource cancellation)
    {
        SelectionProbe? probe;
        try
        {
            probe = await _queryService.QueryAsync(targetWindow, screenPoint, cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DemoTrace.Write("probe_cancel", "cancelled");
            return;
        }
        catch
        {
            DemoTrace.Write("probe_error", "unhandled");
            return;
        }

        if (probe is null || probe.Ignored || (!probe.HasText && !probe.CanUseCopyFallback) || cancellation.IsCancellationRequested)
        {
            DemoTrace.Write("probe_result", $"shown=0;null={probe is null};ignored={probe?.Ignored};length={probe?.Text?.Length ?? 0};fallback={probe?.CanUseCopyFallback};cancelled={cancellation.IsCancellationRequested};reason={probe?.FailureReason ?? "none"}");
            return;
        }

        _uiContext.Post(_ =>
        {
            if (_paused || cancellation.IsCancellationRequested || _button.IsDisposed)
            {
                return;
            }

            if (NativeMethods.GetForegroundWindow() != targetWindow && !_button.Visible)
            {
                DemoTrace.Write("probe_result", "shown=0;foreground_mismatch=1");
                return;
            }

            _activeProbe = probe;
            _button.ShowFor(probe);
            DemoTrace.Write("probe_result", $"shown=1;length={probe.Text?.Length ?? 0};fallback={probe.CanUseCopyFallback}");
        }, null);
    }

    private void OnButtonClicked(SelectionProbe? probe)
    {
        if (probe is null || _paused)
        {
            return;
        }

        _button.Hide();
        _activeProbe = null;
        DemoTrace.Write("button", $"clicked;length={probe.Text?.Length ?? 0};fallback={probe.CanUseCopyFallback}");
        TranslateRequested?.Invoke(probe);
    }

    private void HideIfForegroundChanged()
    {
        if (!_button.Visible || _activeProbe is null)
        {
            return;
        }

        if (NativeMethods.GetForegroundWindow() != _activeProbe.TargetWindow)
        {
            _probeCancellation?.Cancel();
            _button.Hide();
            _activeProbe = null;
            DemoTrace.Write("button", "hidden_foreground_changed");
        }
    }

    private void CancelProbeAndHide()
    {
        _probeCancellation?.Cancel();
        _button.Hide();
        _activeProbe = null;
    }

    private static int DistanceSquared(Point first, Point second)
    {
        var dx = (long)first.X - second.X;
        var dy = (long)first.Y - second.Y;
        return (int)Math.Min(int.MaxValue, dx * dx + dy * dy);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mouseHook.MouseDown -= OnHookMouseDown;
        _mouseHook.MouseUp -= OnHookMouseUp;
        _mouseHook.MouseDoubleClick -= OnHookDoubleClick;
        _button.Clicked -= OnButtonClicked;
        _foregroundTimer.Stop();
        _foregroundTimer.Dispose();
        CancelProbeAndHide();
        _button.Dispose();
        _probeCancellation?.Dispose();
    }
}

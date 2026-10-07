using System.Windows.Automation;
using System.Text;
using System.Runtime.InteropServices;

namespace CherryTranslate.App;

internal sealed record SelectionProbe(
    IntPtr TargetWindow,
    Point ScreenPoint,
    string? Text,
    bool CanUseCopyFallback,
    bool Ignored,
    string? FailureReason = null)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

internal sealed class SelectionQueryService
{
    private static readonly SemaphoreSlim QueryGate = new(2, 2);
    private readonly IntPtr _ownProcessWindow;

    public SelectionQueryService(IntPtr ownProcessWindow)
    {
        _ownProcessWindow = ownProcessWindow;
    }

    public async Task<SelectionProbe?> QueryAsync(
        IntPtr targetWindow,
        Point screenPoint,
        CancellationToken cancellationToken)
    {
        if (targetWindow == IntPtr.Zero || IsOurWindow(targetWindow))
        {
            DemoTrace.Write("uia_skip", "own_or_empty_target");
            return null;
        }

        bool acquired;
        try
        {
            acquired = await QueryGate.WaitAsync(TimeSpan.FromMilliseconds(150), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        // A UIA provider can hang outside our process. Do not start more work when all
        // bounded workers are still occupied by such a provider.
        if (!acquired)
        {
            DemoTrace.Write("uia_gate", "busy");
            return NativeSelectionFallback.Create(targetWindow, screenPoint);
        }

        Task<SelectionProbe> query;
        try
        {
            query = Task.Run(() =>
            {
                try
                {
                    return QueryCore(targetWindow, screenPoint);
                }
                finally
                {
                    // Release when the provider call actually finishes, even if the
                    // caller has already timed out.
                    QueryGate.Release();
                }
            });
        }
        catch
        {
            QueryGate.Release();
            return null;
        }

        try
        {
            var result = await query.WaitAsync(TimeSpan.FromMilliseconds(1_300), cancellationToken)
                .ConfigureAwait(false);
            DemoTrace.Write("uia_result", $"length={result.Text?.Length ?? 0};ignored={result.Ignored};fallback={result.CanUseCopyFallback};reason={result.FailureReason ?? "none"}");
            return result;
        }
        catch (TimeoutException)
        {
            var fallback = NativeSelectionFallback.Create(targetWindow, screenPoint);
            DemoTrace.Write("uia_timeout", $"ms=1300;native_fallback={fallback.CanUseCopyFallback};ignored={fallback.Ignored}");
            return fallback;
        }
        catch (OperationCanceledException)
        {
            DemoTrace.Write("uia_cancel", "cancelled");
            return null;
        }
        catch
        {
            var fallback = NativeSelectionFallback.Create(targetWindow, screenPoint);
            DemoTrace.Write("uia_error", $"provider_error;native_fallback={fallback.CanUseCopyFallback};ignored={fallback.Ignored}");
            return fallback;
        }
    }

    private SelectionProbe QueryCore(IntPtr targetWindow, Point screenPoint)
    {
        AutomationElement? focusedElement = null;
        try
        {
            focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is not null &&
                IsElementInTargetProcess(focusedElement, targetWindow) &&
                IsPassword(focusedElement))
            {
                return new SelectionProbe(
                    targetWindow,
                    screenPoint,
                    null,
                    false,
                    true,
                    "密码输入框不会被读取");
            }
        }
        catch
        {
            // A focused provider may be inaccessible; continue with point lookup and
            // keep copy fallback disabled unless a safe element is established.
        }

        AutomationElement? pointElement = null;
        try
        {
            pointElement = AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            if (pointElement is not null &&
                IsElementInTargetProcess(pointElement, targetWindow) &&
                IsPassword(pointElement))
            {
                return new SelectionProbe(
                    targetWindow,
                    screenPoint,
                    null,
                    false,
                    true,
                    "密码输入框不会被读取");
            }
        }
        catch
        {
            // Some elevated or custom-rendered apps deny point lookup. Copy fallback remains useful.
        }

        var roots = new List<AutomationElement>();
        var safeElementSeen = false;
        if (pointElement is not null)
        {
            roots.Add(pointElement);
            safeElementSeen = IsElementInTargetProcess(pointElement, targetWindow);
        }

        try
        {
            if (focusedElement is not null && IsElementInTargetProcess(focusedElement, targetWindow))
            {
                roots.Add(focusedElement);
                safeElementSeen = true;
            }
        }
        catch
        {
            // FocusedElement is optional; point ancestors can still provide TextPattern.
        }

        try
        {
            var targetRoot = AutomationElement.FromHandle(targetWindow);
            if (targetRoot is not null)
            {
                roots.Add(targetRoot);
            }
        }
        catch
        {
            // Handle lookup can fail for a window in another integrity level.
        }

        var seen = new HashSet<int>();
        var pointBlocksFallback = IsLikelyNonTextSurface(pointElement);

        foreach (var root in roots)
        {
            foreach (var element in Ancestors(root))
            {
                if (!IsElementInTargetProcess(element, targetWindow))
                {
                    continue;
                }

                if (!seen.Add(element.GetHashCode()))
                {
                    continue;
                }

                if (IsPassword(element))
                {
                    return new SelectionProbe(targetWindow, screenPoint, null, false, true, "密码输入框不会被读取");
                }

                if (TryGetSelection(element, out var text))
                {
                    return new SelectionProbe(targetWindow, screenPoint, text, true, false);
                }
            }
        }

        return new SelectionProbe(
            targetWindow,
            screenPoint,
            null,
            safeElementSeen && !pointBlocksFallback,
            false,
            pointBlocksFallback ? "当前鼠标位置像是窗口边框或滚动条" : null);
    }

    private static IEnumerable<AutomationElement> Ancestors(AutomationElement start)
    {
        var current = start;
        for (var depth = 0; depth < 9 && current is not null; depth++)
        {
            yield return current;
            AutomationElement? parent;
            try
            {
                parent = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch
            {
                yield break;
            }

            if (parent is null)
            {
                yield break;
            }

            current = parent;
        }
    }

    private static bool TryGetSelection(AutomationElement element, out string? text)
    {
        text = null;
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern pattern)
            {
                return false;
            }

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0)
            {
                return false;
            }

            var pieces = ranges
                .Select(range => range.GetText(12_001))
                .Where(piece => !string.IsNullOrWhiteSpace(piece))
                .ToArray();
            if (pieces.Length == 0)
            {
                return false;
            }

            text = string.Join(Environment.NewLine, pieces).Trim();
            return text.Length > 0 && text.Length <= 12_000;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPassword(AutomationElement element)
    {
        try
        {
            return element.Current.IsPassword;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyNonTextSurface(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            var type = element.Current.ControlType;
            return type == ControlType.ScrollBar ||
                   type == ControlType.Thumb ||
                   type == ControlType.TitleBar ||
                   type == ControlType.Menu ||
                   type == ControlType.Window;
        }
        catch
        {
            return false;
        }
    }

    private bool IsOurWindow(IntPtr hwnd)
    {
        if (_ownProcessWindow == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var targetProcess);
            NativeMethods.GetWindowThreadProcessId(_ownProcessWindow, out var ownProcess);
            return targetProcess != 0 && targetProcess == ownProcess;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElementInTargetProcess(AutomationElement element, IntPtr targetWindow)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(targetWindow, out var targetProcess);
            return targetProcess != 0 && element.Current.ProcessId == targetProcess;
        }
        catch
        {
            return false;
        }
    }
}

internal static class NativeSelectionFallback
{
    private static readonly string[] KnownTextClasses =
    {
        "edit",
        "richedit",
        "richedit20a",
        "richedit20w",
        "richedit50w",
        "scintilla",
        "chrome_renderwidgethosthwnd",
        "mozillawindowclass",
        "internet explorer_server",
        "webview2"
    };

    public static SelectionProbe Create(IntPtr targetWindow, Point screenPoint)
    {
        InspectionResult result;
        try
        {
            result = TryInspect(targetWindow, screenPoint);
        }
        catch
        {
            result = new InspectionResult(false, false, "", "native_inspection_error");
        }
        DemoTrace.Write(
            "native_fallback",
            $"eligible={result.CanUseCopyFallback};ignored={result.Ignored};class={result.ClassName};reason={result.Reason}");
        return new SelectionProbe(
            targetWindow,
            screenPoint,
            null,
            result.CanUseCopyFallback,
            result.Ignored,
            result.Reason);
    }

    private static InspectionResult TryInspect(IntPtr targetWindow, Point screenPoint)
    {
        if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
        {
            return new InspectionResult(false, false, "", "invalid_target");
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(targetWindow, out var targetProcess);
        if (threadId == 0 || targetProcess == 0)
        {
            return new InspectionResult(false, false, "", "target_process_unavailable");
        }

        var guiInfo = new NativeMethods.GuiThreadInfo
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref guiInfo))
        {
            return new InspectionResult(false, false, "", "gui_thread_unavailable");
        }

        var focused = guiInfo.HwndFocus;
        if (focused == IntPtr.Zero || focused == targetWindow || !NativeMethods.IsWindow(focused))
        {
            return new InspectionResult(false, false, "", "no_child_focus");
        }

        NativeMethods.GetWindowThreadProcessId(focused, out var focusedProcess);
        if (focusedProcess != targetProcess || NativeMethods.GetAncestor(focused, NativeMethods.GA_ROOT) != targetWindow)
        {
            return new InspectionResult(false, false, "", "focus_outside_target");
        }

        var className = ReadClassName(focused);
        var style = NativeMethods.GetWindowLongPtr(focused, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.ES_PASSWORD) != 0 || className.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return new InspectionResult(false, true, className, "password_control");
        }

        var eligible = IsKnownTextClass(className);
        return new InspectionResult(
            eligible,
            false,
            className,
            eligible ? "known_text_control" : "unknown_focus_class");
    }

    private static bool IsKnownTextClass(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        var normalized = className.Trim().ToLowerInvariant();
        return KnownTextClasses.Any(known =>
            normalized.Equals(known, StringComparison.Ordinal) ||
            normalized.StartsWith("windowsforms10." + known, StringComparison.Ordinal) ||
            normalized.StartsWith(known + ".", StringComparison.Ordinal));
    }

    private static string ReadClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : "";
    }

    private readonly record struct InspectionResult(
        bool CanUseCopyFallback,
        bool Ignored,
        string ClassName,
        string Reason);
}

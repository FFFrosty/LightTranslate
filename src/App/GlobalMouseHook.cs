using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CherryTranslate.App;

internal readonly record struct MouseEventInfo(Point ScreenPoint, IntPtr ForegroundWindow, long Timestamp);

internal sealed class GlobalMouseHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _callback;
    private IntPtr _hook;
    private bool _disposed;

    public GlobalMouseHook()
    {
        _callback = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _callback,
            NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName),
            0);

        if (_hook == IntPtr.Zero)
        {
            DemoTrace.Write("hook", "install_failed");
            throw new InvalidOperationException("无法安装系统鼠标监听。");
        }

        DemoTrace.Write("hook", "installed");
    }

    public event Action<MouseEventInfo>? MouseDown;
    public event Action<MouseEventInfo>? MouseUp;
    public event Action<MouseEventInfo>? MouseDoubleClick;

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var raw = Marshal.PtrToStructure<NativeMethods.MsllHookStruct>(lParam);
                var info = new MouseEventInfo(
                    new Point(raw.Pt.X, raw.Pt.Y),
                    NativeMethods.GetForegroundWindow(),
                    Stopwatch.GetTimestamp());

                var action = wParam.ToInt32() switch
                {
                    NativeMethods.WM_LBUTTONDOWN => "down",
                    NativeMethods.WM_LBUTTONUP => "up",
                    NativeMethods.WM_LBUTTONDBLCLK => "double",
                    _ => null
                };
                if (action is not null)
                {
                    DemoTrace.Write("hook_event", $"action={action};x={info.ScreenPoint.X};y={info.ScreenPoint.Y};fg=0x{info.ForegroundWindow.ToInt64():X}");
                }

                switch (wParam.ToInt32())
                {
                    case NativeMethods.WM_LBUTTONDOWN:
                        MouseDown?.Invoke(info);
                        break;
                    case NativeMethods.WM_LBUTTONUP:
                        MouseUp?.Invoke(info);
                        break;
                    case NativeMethods.WM_LBUTTONDBLCLK:
                        MouseDoubleClick?.Invoke(info);
                        break;
                }
            }
            catch
            {
                // Never let a callback exception interfere with the user's mouse input.
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            DemoTrace.Write("hook", "uninstalled");
        }
        GC.SuppressFinalize(this);
    }
}

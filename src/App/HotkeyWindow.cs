namespace CherryTranslate.App;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x4C54;
    private bool _registered;
    private bool _disposed;

    public HotkeyWindow(Action callback)
    {
        Callback = callback;
        CreateHandle(new CreateParams());
        _registered = NativeMethods.RegisterHotKey(
            Handle,
            HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_T);
    }

    private Action Callback { get; }

    public bool IsRegistered => _registered;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            try
            {
                Callback();
            }
            catch
            {
                // A hotkey callback is user input; errors are handled by the host.
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
        DestroyHandle();
        GC.SuppressFinalize(this);
    }
}

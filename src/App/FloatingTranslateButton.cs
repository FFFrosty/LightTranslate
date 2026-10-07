namespace CherryTranslate.App;

internal sealed class FloatingTranslateButton : Form
{
    private readonly Button _button;
    private readonly ToolTip _toolTip;
    private SelectionProbe? _probe;

    public FloatingTranslateButton()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(42, 120, 218);
        Padding = new Padding(1);

        _button = new Button
        {
            Text = "译",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(42, 120, 218),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            TabStop = false,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        _button.FlatAppearance.BorderSize = 0;
        _button.Click += (_, _) => Clicked?.Invoke(_probe);
        Controls.Add(_button);

        _toolTip = new ToolTip { InitialDelay = 250, AutoPopDelay = 5000 };
        _toolTip.SetToolTip(_button, "点击翻译；不会自动读取文本");
        Size = new Size(32, 32);
    }

    public event Action<SelectionProbe?>? Clicked;

    public bool IsPointInside(Point point)
        => Visible && Bounds.Contains(point);

    public void ShowFor(SelectionProbe probe)
    {
        _probe = probe;
        DemoTrace.Write("button_position", $"x={probe.ScreenPoint.X};y={probe.ScreenPoint.Y};length={probe.Text?.Length ?? 0}");
        _toolTip.SetToolTip(
            _button,
            probe.HasText
                ? "点击翻译"
                : "点击后从原窗口复制选区并翻译");

        if (!IsHandleCreated)
        {
            CreateControl();
        }

        var dpi = NativeMethods.GetDpiForWindow(probe.TargetWindow);
        if (dpi == 0)
        {
            dpi = NativeMethods.GetDpiForWindow(Handle);
        }
        if (dpi == 0)
        {
            dpi = 96;
        }
        var size = Math.Max(26, (int)Math.Round(32 * dpi / 96d));
        var screen = Screen.FromPoint(probe.ScreenPoint);
        var x = probe.ScreenPoint.X + Math.Max(6, size / 4);
        var y = probe.ScreenPoint.Y - size - Math.Max(6, size / 4);
        x = Math.Clamp(x, screen.WorkingArea.Left, screen.WorkingArea.Right - size);
        y = Math.Clamp(y, screen.WorkingArea.Top, screen.WorkingArea.Bottom - size);
        Size = new Size(size, size);

        if (!Visible)
        {
            Show();
        }

        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            size,
            size,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
    }

    public new void Hide()
    {
        _probe = null;
        base.Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Prevent an accidental activate through a native mouse action.
        if (m.Msg == 0x0086 && m.WParam == IntPtr.Zero)
        {
            base.WndProc(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}

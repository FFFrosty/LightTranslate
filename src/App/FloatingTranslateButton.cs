using System.Drawing.Drawing2D;

namespace CherryTranslate.App;

internal sealed class FloatingTranslateButton : Form
{
    private readonly TranslateToolbarButton _button;
    private readonly ToolTip _toolTip;
    private SelectionProbe? _probe;

    public FloatingTranslateButton()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = ThemeManager.Palette.Surface;
        Padding = new Padding(0);
        Name = "FloatingTranslateToolbar";
        AccessibleName = "轻译翻译工具条";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _button = new TranslateToolbarButton();
        _button.Click += (_, _) => Clicked?.Invoke(_probe);
        Controls.Add(_button);

        _toolTip = new ToolTip { InitialDelay = 250, AutoPopDelay = 5000 };
        _toolTip.SetToolTip(_button, "点击翻译；不会自动读取文本");
        Size = new Size(112, 36);
        ThemeManager.Changed += OnThemeChanged;
        Resize += (_, _) => UiDrawing.ApplyRoundedRegion(this, ScaleLogical(10));
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
        var width = Math.Max(86, (int)Math.Round(112 * dpi / 96d));
        var height = Math.Max(30, (int)Math.Round(36 * dpi / 96d));
        var margin = Math.Max(6, (int)Math.Round(8 * dpi / 96d));
        var screen = Screen.FromPoint(probe.ScreenPoint);
        var x = probe.ScreenPoint.X - width / 2;
        var aboveY = probe.ScreenPoint.Y - height - margin;
        var belowY = probe.ScreenPoint.Y + margin;
        var y = aboveY >= screen.WorkingArea.Top ? aboveY : belowY;
        x = Math.Clamp(x, screen.WorkingArea.Left, screen.WorkingArea.Right - width);
        y = Math.Clamp(y, screen.WorkingArea.Top, screen.WorkingArea.Bottom - height);
        Size = new Size(width, height);

        if (!Visible)
        {
            Show();
        }

        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
    }

    public new void Hide()
    {
        _probe = null;
        base.Hide();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        UiDrawing.FillRounded(e.Graphics, ClientRectangle, ThemeManager.Palette.Surface, ScaleLogical(10));
    }

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
            ThemeManager.Changed -= OnThemeChanged;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        BackColor = ThemeManager.Palette.Surface;
        _button.Invalidate();
        Invalidate();
    }

    private int ScaleLogical(int value)
        => Math.Max(2, (int)Math.Round(value * (DeviceDpi <= 0 ? 96 : DeviceDpi) / 96d));
}

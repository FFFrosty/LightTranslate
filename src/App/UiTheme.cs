using System.Drawing.Drawing2D;
using System.Text;
using Microsoft.Win32;

namespace CherryTranslate.App;

internal enum ThemeMode
{
    Follow,
    Light,
    Dark
}

internal readonly record struct UiPalette(
    Color Surface,
    Color Card,
    Color Muted,
    Color Text,
    Color SecondaryText,
    Color Border,
    Color Accent,
    Color AccentSurface,
    Color Error,
    Color CodeBackground)
{
    public static UiPalette Light { get; } = new(
        Color.FromArgb(255, 255, 255),
        Color.FromArgb(248, 248, 248),
        Color.FromArgb(242, 242, 242),
        Color.FromArgb(24, 24, 24),
        Color.FromArgb(115, 115, 115),
        Color.FromArgb(224, 224, 224),
        Color.FromArgb(67, 212, 86),
        Color.FromArgb(233, 248, 235),
        Color.FromArgb(190, 55, 55),
        Color.FromArgb(245, 245, 245));

    public static UiPalette Dark { get; } = new(
        Color.FromArgb(38, 38, 38),
        Color.FromArgb(23, 23, 23),
        Color.FromArgb(55, 55, 55),
        Color.FromArgb(242, 242, 242),
        Color.FromArgb(170, 170, 170),
        Color.FromArgb(70, 70, 70),
        Color.FromArgb(93, 205, 108),
        Color.FromArgb(46, 77, 50),
        Color.FromArgb(245, 115, 115),
        Color.FromArgb(48, 48, 48));
}

internal static class ThemeManager
{
    private const string RegistryPath = "Software\\LightTranslate";
    private static ThemeMode _mode = LoadMode();

    public static event EventHandler? Changed;

    public static ThemeMode Mode => _mode;

    public static bool IsDark => _mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => DetectWindowsDarkMode()
    };

    public static UiPalette Palette => IsDark ? UiPalette.Dark : UiPalette.Light;

    public static void SetMode(ThemeMode mode, bool persist = true)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        if (!persist)
        {
            Changed?.Invoke(null, EventArgs.Empty);
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue("ThemeMode", mode.ToString(), RegistryValueKind.String);
        }
        catch
        {
            // Theme choice remains active for this process when the registry is unavailable.
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void RefreshFromSystem()
    {
        if (_mode == ThemeMode.Follow)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static ThemeMode LoadMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var value = key?.GetValue("ThemeMode") as string;
            return Enum.TryParse<ThemeMode>(value, true, out var mode) ? mode : ThemeMode.Follow;
        }
        catch
        {
            return ThemeMode.Follow;
        }
    }

    private static bool DetectWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        catch
        {
            return false;
        }
    }
}

internal static class UiTheme
{
    public static void ApplyControls(Control root)
    {
        var palette = ThemeManager.Palette;
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case UiIconButton:
                case UiTextButton:
                case TranslateToolbarButton:
                    control.Invalidate();
                    break;
                case Label label:
                    label.ForeColor = palette.Text;
                    break;
                case ComboBox combo:
                    combo.BackColor = palette.Card;
                    combo.ForeColor = palette.Text;
                    combo.Invalidate();
                    break;
                case TextBoxBase textBox:
                    textBox.BackColor = control.Name.Contains("Source", StringComparison.OrdinalIgnoreCase)
                        ? palette.Muted
                        : palette.Card;
                    textBox.ForeColor = control.Name.Contains("Source", StringComparison.OrdinalIgnoreCase)
                        ? palette.SecondaryText
                        : palette.Text;
                    break;
                case TrackBar trackBar:
                    trackBar.BackColor = palette.Card;
                    break;
                case Panel panel:
                    panel.BackColor = IsCardSurface(panel.Name) ? palette.Card : palette.Surface;
                    break;
            }

            ApplyControls(control);
        }
    }

    private static bool IsCardSurface(string name)
        => name.Contains("Card", StringComparison.OrdinalIgnoreCase)
           || name.Contains("SettingsPanel", StringComparison.OrdinalIgnoreCase)
           || name.Contains("OpacityPopup", StringComparison.OrdinalIgnoreCase);
}

internal enum UiIcon
{
    Pin,
    Droplet,
    Minus,
    Close,
    Copy,
    Refresh,
    Stop,
    ChevronDown,
    Globe,
    ArrowRight,
    Settings,
    Translate
}

internal static class UiDrawing
{
    public static int Scale(Control control, int logical)
        => Math.Max(1, (int)Math.Round(logical * (control.DeviceDpi <= 0 ? 96 : control.DeviceDpi) / 96d));

    public static void FillRounded(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        using var path = RoundedPath(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedBorder(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        using var path = RoundedPath(bounds, radius);
        using var pen = new Pen(color, 1f);
        graphics.DrawPath(pen, path);
    }

    public static void ApplyRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0)
        {
            return;
        }

        using var path = RoundedPath(new Rectangle(0, 0, control.Width, control.Height), radius);
        control.Region?.Dispose();
        control.Region = new Region(path);
    }

    public static void DrawIcon(Graphics graphics, UiIcon icon, Rectangle bounds, Color color, float width = 1.45f)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        var x = bounds.X;
        var y = bounds.Y;
        var w = bounds.Width;
        var h = bounds.Height;
        var cx = x + w / 2f;
        var cy = y + h / 2f;

        switch (icon)
        {
            case UiIcon.Pin:
                graphics.DrawLine(pen, cx, y + 2, cx, y + h - 1);
                graphics.DrawLine(pen, x + 3, y + 5, x + w - 3, y + 5);
                graphics.DrawLine(pen, x + 5, y + 5, x + 5, y + 9);
                graphics.DrawLine(pen, x + w - 5, y + 5, x + w - 5, y + 9);
                graphics.DrawLine(pen, x + 5, y + 9, x + w - 5, y + 9);
                graphics.DrawLine(pen, cx, y + 9, cx, y + h - 2);
                break;
            case UiIcon.Droplet:
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(cx, y + 1, x + 3, y + 7, x + 3, y + h - 2, cx, y + h - 2);
                    path.AddBezier(cx, y + h - 2, x + w - 3, y + h - 2, x + w - 3, y + 7, cx, y + 1);
                    graphics.DrawPath(pen, path);
                }
                break;
            case UiIcon.Minus:
                graphics.DrawLine(pen, x + 3, cy, x + w - 3, cy);
                break;
            case UiIcon.Close:
                graphics.DrawLine(pen, x + 3, y + 3, x + w - 3, y + h - 3);
                graphics.DrawLine(pen, x + w - 3, y + 3, x + 3, y + h - 3);
                break;
            case UiIcon.Copy:
                graphics.DrawRectangle(pen, x + 4, y + 2, w - 7, h - 6);
                graphics.DrawLine(pen, x + 2, y + 5, x + 2, y + h - 2);
                graphics.DrawLine(pen, x + 2, y + h - 2, x + w - 4, y + h - 2);
                break;
            case UiIcon.Refresh:
                graphics.DrawArc(pen, x + 2, y + 2, w - 4, h - 4, 35, 285);
                graphics.DrawLine(pen, x + w - 4, y + 2, x + w - 4, y + 7);
                graphics.DrawLine(pen, x + w - 4, y + 2, x + w - 9, y + 2);
                break;
            case UiIcon.Stop:
                graphics.DrawRectangle(pen, x + 4, y + 4, w - 8, h - 8);
                break;
            case UiIcon.ChevronDown:
                graphics.DrawLine(pen, x + 3, y + 5, cx, y + h - 4);
                graphics.DrawLine(pen, cx, y + h - 4, x + w - 3, y + 5);
                break;
            case UiIcon.Globe:
                graphics.DrawEllipse(pen, x + 1, y + 1, w - 2, h - 2);
                graphics.DrawLine(pen, cx, y + 2, cx, y + h - 2);
                graphics.DrawArc(pen, x + 4, y + 1, w - 8, h - 2, 90, 180);
                graphics.DrawArc(pen, x + 4, y + 1, w - 8, h - 2, 270, 180);
                graphics.DrawLine(pen, x + 2, cy, x + w - 2, cy);
                break;
            case UiIcon.ArrowRight:
                graphics.DrawLine(pen, x + 2, cy, x + w - 3, cy);
                graphics.DrawLine(pen, x + w - 7, y + 4, x + w - 2, cy);
                graphics.DrawLine(pen, x + w - 2, cy, x + w - 7, y + h - 4);
                break;
            case UiIcon.Settings:
                graphics.DrawEllipse(pen, x + 4, y + 4, w - 8, h - 8);
                graphics.DrawLine(pen, cx, y + 1, cx, y + 4);
                graphics.DrawLine(pen, cx, y + h - 4, cx, y + h - 1);
                graphics.DrawLine(pen, x + 1, cy, x + 4, cy);
                graphics.DrawLine(pen, x + w - 4, cy, x + w - 1, cy);
                break;
            case UiIcon.Translate:
                graphics.DrawLine(pen, x + 3, y + 4, x + w - 3, y + 4);
                graphics.DrawLine(pen, x + 6, y + 1, x + 6, y + 7);
                graphics.DrawLine(pen, x + 3, y + 11, x + 8, y + 6);
                graphics.DrawLine(pen, x + 8, y + 6, x + 12, y + 11);
                graphics.DrawLine(pen, x + w - 7, y + 7, x + w - 3, y + 7);
                graphics.DrawLine(pen, x + w - 5, y + 5, x + w - 5, y + 9);
                break;
        }
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class UiIconButton : Button
{
    private bool _hover;

    public UiIconButton(UiIcon icon, string name, string accessibleName)
    {
        Icon = icon;
        Name = name;
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.PushButton;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        ForeColor = ThemeManager.Palette.SecondaryText;
        Cursor = Cursors.Hand;
        Size = new Size(28, 26);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public UiIcon Icon { get; }

    public bool IsActive { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var palette = ThemeManager.Palette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hover || IsActive)
        {
            UiDrawing.FillRounded(e.Graphics, ClientRectangle, palette.AccentSurface, UiDrawing.Scale(this, 6));
        }

        var iconSize = UiDrawing.Scale(this, 14);
        UiDrawing.DrawIcon(
            e.Graphics,
            Icon,
            new Rectangle((Width - iconSize) / 2, (Height - iconSize) / 2, iconSize, iconSize),
            IsActive ? palette.Accent : _hover ? palette.Text : palette.SecondaryText);
    }
}

internal sealed class UiTextButton : Button
{
    private bool _hover;
    private bool _clampingSize;

    public UiTextButton(string name, string accessibleName, string text, UiIcon? icon = null)
    {
        Name = name;
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.PushButton;
        Text = text;
        Icon = icon;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        ForeColor = ThemeManager.Palette.SecondaryText;
        Cursor = Cursors.Hand;
        AutoSize = true;
        MinimumSize = new Size(42, 26);
        Padding = new Padding(8, 2, 8, 2);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public UiIcon? Icon { get; }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ClampToFlowHeight();
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        ClampToFlowHeight();
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (Parent is FlowLayoutPanel flow && flow.ClientSize.Height > 0)
        {
            var availableHeight = flow.ClientSize.Height - flow.Padding.Vertical - Margin.Vertical;
            if (availableHeight > 0)
            {
                height = Math.Min(height, availableHeight);
            }
        }

        base.SetBoundsCore(x, y, width, height, specified);
    }

    private void ClampToFlowHeight()
    {
        if (_clampingSize || Parent is not FlowLayoutPanel flow || flow.ClientSize.Height <= 0)
        {
            return;
        }

        var availableHeight = flow.ClientSize.Height - flow.Padding.Vertical - Margin.Vertical;
        if (availableHeight <= 0 || Height <= availableHeight)
        {
            return;
        }

        _clampingSize = true;
        try
        {
            Height = availableHeight;
        }
        finally
        {
            _clampingSize = false;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var palette = ThemeManager.Palette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var background = _hover ? palette.AccentSurface : palette.Muted;
        UiDrawing.FillRounded(e.Graphics, ClientRectangle, background, UiDrawing.Scale(this, 6));
        if (Icon is { } icon)
        {
            var iconSize = UiDrawing.Scale(this, 14);
            UiDrawing.DrawIcon(e.Graphics, icon,
                new Rectangle(UiDrawing.Scale(this, 8), (Height - iconSize) / 2, iconSize, iconSize),
                palette.SecondaryText);
        }

        using var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        var iconWidth = Icon is null ? 0 : UiDrawing.Scale(this, 20);
        var left = UiDrawing.Scale(this, 8) + iconWidth;
        TextRenderer.DrawText(e.Graphics, Text, font, new Rectangle(left, 0, Math.Max(1, Width - left - UiDrawing.Scale(this, 6)), Height),
            palette.SecondaryText, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        using var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        var measured = TextRenderer.MeasureText(Text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        var iconWidth = Icon is null ? 0 : UiDrawing.Scale(this, 20);
        var horizontalPadding = UiDrawing.Scale(this, 14);
        var height = Math.Max(UiDrawing.Scale(this, 26), measured.Height + UiDrawing.Scale(this, 6));
        return new Size(measured.Width + iconWidth + horizontalPadding, height);
    }
}

internal sealed class UiFlowLayoutPanel : FlowLayoutPanel
{
    private bool _clamping;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_clamping || ClientSize.Height <= 0)
        {
            return;
        }

        var availableHeight = ClientSize.Height - Padding.Vertical;
        if (availableHeight <= 0)
        {
            return;
        }

        _clamping = true;
        try
        {
            foreach (Control child in Controls)
            {
                var maximumHeight = Math.Max(1, availableHeight - child.Margin.Vertical);
                if (child.Height > maximumHeight)
                {
                    child.Height = maximumHeight;
                }
            }
        }
        finally
        {
            _clamping = false;
        }
    }
}

internal sealed class TranslateToolbarButton : Button
{
    private bool _hover;

    public TranslateToolbarButton()
    {
        Text = "译";
        Name = "TranslateButton";
        AccessibleName = "翻译";
        AccessibleRole = AccessibleRole.PushButton;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var palette = ThemeManager.Palette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Math.Max(1d, Height / 36d);
        var radius = Math.Max(2, (int)Math.Round(9 * scale));
        var fill = _hover ? palette.AccentSurface : palette.Card;
        UiDrawing.FillRounded(e.Graphics, ClientRectangle, fill, radius);
        UiDrawing.DrawRoundedBorder(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), palette.Border, radius);

        var mark = new Rectangle((int)Math.Round(8 * scale), (Height - (int)Math.Round(20 * scale)) / 2,
            (int)Math.Round(20 * scale), (int)Math.Round(20 * scale));
        UiDrawing.FillRounded(e.Graphics, mark, palette.Accent, Math.Max(2, (int)Math.Round(6 * scale)));
        using (var markFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point))
        using (var markBrush = new SolidBrush(Color.White))
        {
            TextRenderer.DrawText(e.Graphics, "轻", markFont, mark, markBrush.Color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        var iconX = (int)Math.Round(35 * scale);
        var iconSize = Math.Max(8, (int)Math.Round(15 * scale));
        UiDrawing.DrawIcon(e.Graphics, UiIcon.Translate, new Rectangle(iconX, (Height - iconSize) / 2, iconSize, iconSize), palette.Text);
        using var font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        var textX = (int)Math.Round(55 * scale);
        TextRenderer.DrawText(e.Graphics, "翻译", font, new Rectangle(textX, 0, Math.Max(16, Width - textX - (int)Math.Round(4 * scale)), Height), palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

internal sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

internal static class LanguageCatalog
{
    public static IReadOnlyList<LanguageOption> Options { get; } = new[]
    {
        new LanguageOption("zh-cn", "简体中文"),
        new LanguageOption("zh-tw", "繁體中文"),
        new LanguageOption("en-us", "English"),
        new LanguageOption("ja-jp", "日本語"),
        new LanguageOption("ko-kr", "한국어"),
        new LanguageOption("fr-fr", "Français"),
        new LanguageOption("de-de", "Deutsch"),
        new LanguageOption("es-es", "Español"),
        new LanguageOption("ru-ru", "Русский"),
        new LanguageOption("pt-pt", "Português"),
        new LanguageOption("it-it", "Italiano"),
        new LanguageOption("ar-sa", "العربية"),
        new LanguageOption("auto", "自动检测")
    };

    public static IReadOnlyList<LanguageOption> TargetOptions
        => Options.Where(item => !string.Equals(item.Code, "auto", StringComparison.OrdinalIgnoreCase)).ToArray();

    public static LanguageOption Find(string? code)
    {
        var normalized = Normalize(code);
        return Options.FirstOrDefault(item => string.Equals(item.Code, normalized, StringComparison.OrdinalIgnoreCase))
               ?? new LanguageOption(normalized, normalized);
    }

    public static LanguageOption FindTarget(string? code)
    {
        var option = Find(code);
        return string.Equals(option.Code, "auto", StringComparison.OrdinalIgnoreCase)
            ? Options[0]
            : option;
    }

    public static string Detect(string text)
    {
        var detected = CherryTranslate.Core.TranslationClient.DetectSourceLanguage(text);
        return Normalize(detected);
    }

    public static bool Matches(string detectedCode, string languageCode)
    {
        var detected = Normalize(detectedCode);
        var language = Normalize(languageCode);
        if (detected == "auto" || language == "auto" || detected.Length < 2 || language.Length < 2)
        {
            return false;
        }

        return detected[..2] == language[..2];
    }

    private static string Normalize(string? code)
    {
        var value = code?.Trim().ToLowerInvariant() ?? "auto";
        return value switch
        {
            "zh" => "zh-cn",
            "en" => "en-us",
            "ja" => "ja-jp",
            "ko" => "ko-kr",
            "fr" => "fr-fr",
            "de" => "de-de",
            "es" => "es-es",
            "ru" => "ru-ru",
            "pt" => "pt-pt",
            "it" => "it-it",
            "ar" => "ar-sa",
            _ => value
        };
    }
}

internal static class MarkdownRichTextRenderer
{
    public static void Render(RichTextBox box, string markdown, UiPalette palette)
    {
        var selectionStart = Math.Clamp(box.SelectionStart, 0, box.TextLength);
        var selectionLength = Math.Clamp(box.SelectionLength, 0, Math.Max(0, box.TextLength - selectionStart));
        var firstVisibleLine = box.IsHandleCreated
            ? NativeMethods.SendMessage(box.Handle, NativeMethods.EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32()
            : 0;
        var rtf = new System.Text.StringBuilder("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Microsoft YaHei UI;}{\\f1 Consolas;}}{\\colortbl ;");
        AppendColor(rtf, palette.Text);
        AppendColor(rtf, palette.SecondaryText);
        AppendColor(rtf, palette.CodeBackground);
        rtf.Append("}\\viewkind4\\uc1\\cf1\\f0\\fs21 ");

        var inCode = false;
        foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                rtf.Append("\\par ");
                continue;
            }

            if (inCode)
            {
                rtf.Append("\\highlight3\\f1\\fs19 ");
                AppendEscaped(rtf, line);
                rtf.Append("\\highlight0\\f0\\fs21\\par ");
                continue;
            }

            var heading = 0;
            while (heading < line.Length && heading < 3 && line[heading] == '#')
            {
                heading++;
            }

            if (heading > 0 && heading < line.Length && line[heading] == ' ')
            {
                line = line[(heading + 1)..];
                rtf.Append("\\b\\fs").Append(heading == 1 ? 28 : 24).Append(' ');
                AppendInline(rtf, line);
                rtf.Append("\\b0\\fs21\\par ");
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                rtf.Append("\\bullet\\tab ");
                line = line[2..];
            }

            AppendInline(rtf, line);
            rtf.Append("\\par ");
        }

        rtf.Append('}');
        try
        {
            box.Rtf = rtf.ToString();
            var restoredStart = Math.Min(selectionStart, box.TextLength);
            var restoredLength = Math.Min(selectionLength, Math.Max(0, box.TextLength - restoredStart));
            box.Select(restoredStart, restoredLength);
            if (firstVisibleLine > 0 && box.IsHandleCreated)
            {
                var currentFirstLine = NativeMethods.SendMessage(
                    box.Handle,
                    NativeMethods.EM_GETFIRSTVISIBLELINE,
                    IntPtr.Zero,
                    IntPtr.Zero).ToInt32();
                var delta = firstVisibleLine - currentFirstLine;
                if (delta != 0)
                {
                    NativeMethods.SendMessage(
                        box.Handle,
                        NativeMethods.EM_LINESCROLL,
                        IntPtr.Zero,
                        new IntPtr(delta));
                }
            }
        }
        catch
        {
            box.Text = markdown;
        }
    }

    private static void AppendInline(System.Text.StringBuilder rtf, string line)
    {
        var position = 0;
        while (position < line.Length)
        {
            var start = line.IndexOf("**", position, StringComparison.Ordinal);
            if (start < 0)
            {
                AppendEscaped(rtf, line[position..]);
                break;
            }

            AppendEscaped(rtf, line[position..start]);
            var end = line.IndexOf("**", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                AppendEscaped(rtf, line[start..]);
                break;
            }

            rtf.Append("\\b ");
            AppendEscaped(rtf, line[(start + 2)..end]);
            rtf.Append("\\b0 ");
            position = end + 2;
        }
    }

    private static void AppendEscaped(System.Text.StringBuilder rtf, string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\\' or '{' or '}')
            {
                rtf.Append('\\').Append((char)rune.Value);
            }
            else if (rune.Value == '\t')
            {
                rtf.Append("\\tab ");
            }
            else if (rune.Value is >= 32 and <= 126)
            {
                rtf.Append((char)rune.Value);
            }
            else
            {
                if (rune.Value > 0xffff)
                {
                    var scalar = rune.Value - 0x1_0000;
                    var high = (short)(0xd800 + (scalar >> 10));
                    var low = (short)(0xdc00 + (scalar & 0x3ff));
                    AppendRtfUnicode(rtf, high);
                    AppendRtfUnicode(rtf, low);
                }
                else
                {
                    AppendRtfUnicode(rtf, (short)rune.Value);
                }
            }
        }
    }

    private static void AppendRtfUnicode(System.Text.StringBuilder rtf, short value)
        => rtf.Append("\\u").Append(value).Append('?');

    private static void AppendColor(System.Text.StringBuilder rtf, Color color)
        => rtf.Append('\\').Append("red").Append(color.R).Append('\\').Append("green").Append(color.G).Append('\\').Append("blue").Append(color.B).Append(';');
}

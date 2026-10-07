using CherryTranslate.Core;
using System.Drawing.Drawing2D;
using System.Net.Http;

namespace CherryTranslate.App;

internal delegate Task<string> StreamingTranslateHandler(
    string? targetLanguageOverride,
    IProgress<string>? progress,
    CancellationToken cancellationToken);

internal abstract class CherryForm : Form
{
    private readonly Panel _surface;
    private readonly Label _titleLabel;
    private readonly ToolTip _toolTips = new();
    private bool _dragging;
    private bool _initialDpiScaleApplied;

    protected CherryForm(string title, Size logicalSize, Size minimumSize, bool showPin, bool showOpacity)
    {
        Text = title;
        Name = title.Replace(' ', '_');
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = logicalSize;
        MinimumSize = minimumSize;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        KeyPreview = true;
        BackColor = ThemeManager.Palette.Surface;
        Padding = new Padding(0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _surface = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            BackColor = ThemeManager.Palette.Surface
        };
        _surface.Paint += (_, e) =>
        {
            var palette = ThemeManager.Palette;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            UiDrawing.FillRounded(e.Graphics, _surface.ClientRectangle, palette.Surface, ScaleLogical(8));
            UiDrawing.DrawRoundedBorder(e.Graphics,
                new Rectangle(0, 0, Math.Max(1, _surface.Width - 1), Math.Max(1, _surface.Height - 1)),
                palette.Border,
                ScaleLogical(8));
        };

        TitleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = ThemeManager.Palette.Muted,
            Name = "TitleBar"
        };
        TitleBar.MouseDown += TitleBarMouseDown;
        TitleBar.MouseMove += TitleBarMouseMove;
        TitleBar.MouseUp += (_, _) => _dragging = false;

        _titleLabel = new Label
        {
            Text = title,
            Name = "Title",
            AccessibleName = title,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ThemeManager.Palette.Text,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
        };
        _titleLabel.MouseDown += TitleBarMouseDown;
        _titleLabel.MouseMove += TitleBarMouseMove;
        _titleLabel.MouseUp += (_, _) => _dragging = false;

        TitleActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = showPin || showOpacity ? 132 : 66,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 3, 4, 3),
            Margin = new Padding(0),
            BackColor = Color.Transparent
        };

        if (showPin)
        {
            PinButton = new UiIconButton(UiIcon.Pin, "Pin", "置顶");
            PinButton.Click += (_, _) =>
            {
                TopMost = !TopMost;
                PinButton.IsActive = TopMost;
                PinButton.Invalidate();
            };
            _toolTips.SetToolTip(PinButton, "置顶");
            TitleActions.Controls.Add(PinButton);
        }

        if (showOpacity)
        {
            OpacityButton = new UiIconButton(UiIcon.Droplet, "Opacity", "透明度");
            OpacityButton.Click += (_, _) => OnOpacityRequested();
            _toolTips.SetToolTip(OpacityButton, "透明度");
            TitleActions.Controls.Add(OpacityButton);
        }

        MinimizeButton = new UiIconButton(UiIcon.Minus, "Minimize", "最小化");
        MinimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _toolTips.SetToolTip(MinimizeButton, "最小化");
        TitleActions.Controls.Add(MinimizeButton);

        CloseButton = new UiIconButton(UiIcon.Close, "Close", "关闭");
        CloseButton.Click += (_, _) => Close();
        _toolTips.SetToolTip(CloseButton, "关闭");
        TitleActions.Controls.Add(CloseButton);

        TitleBar.Controls.Add(_titleLabel);
        TitleBar.Controls.Add(TitleActions);

        ContentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = ThemeManager.Palette.Surface,
            Name = "Content"
        };
        _surface.Controls.Add(ContentPanel);
        _surface.Controls.Add(TitleBar);
        Controls.Add(_surface);

        ThemeManager.Changed += OnThemeChanged;
        Resize += (_, _) => UiDrawing.ApplyRoundedRegion(this, ScaleLogical(8));
    }

    protected Panel ContentPanel { get; }

    protected Panel TitleBar { get; }

    protected FlowLayoutPanel TitleActions { get; }

    protected UiIconButton? PinButton { get; }

    protected UiIconButton? OpacityButton { get; }

    protected UiIconButton MinimizeButton { get; }

    protected UiIconButton CloseButton { get; }

    protected virtual void OnOpacityRequested()
    {
    }

    protected virtual void ApplyPalette()
    {
        var palette = ThemeManager.Palette;
        BackColor = palette.Surface;
        _surface.BackColor = palette.Surface;
        ContentPanel.BackColor = palette.Surface;
        TitleBar.BackColor = palette.Muted;
        _titleLabel.ForeColor = palette.Text;
        UiTheme.ApplyControls(ContentPanel);
        _surface.Invalidate();
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_initialDpiScaleApplied && DeviceDpi > 96)
        {
            _initialDpiScaleApplied = true;
            AutoScaleDimensions = new SizeF(96F, 96F);
            PerformAutoScale();
        }

        UiDrawing.ApplyRoundedRegion(this, ScaleLogical(8));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_THEMECHANGED)
        {
            ThemeManager.RefreshFromSystem();
        }

        base.WndProc(ref m);
        if (m.Msg != NativeMethods.WM_NCHITTEST || WindowState == FormWindowState.Maximized)
        {
            return;
        }

        var point = PointToClient(Cursor.Position);
        var edge = ScaleLogical(6);
        var left = point.X <= edge;
        var right = point.X >= Width - edge;
        var top = point.Y <= edge;
        var bottom = point.Y >= Height - edge;
        m.Result = new IntPtr(
            left
                ? top ? NativeMethods.HTTOPLEFT : bottom ? NativeMethods.HTBOTTOMLEFT : NativeMethods.HTLEFT
                : right
                    ? top ? NativeMethods.HTTOPRIGHT : bottom ? NativeMethods.HTBOTTOMRIGHT : NativeMethods.HTRIGHT
                    : top ? NativeMethods.HTTOP : bottom ? NativeMethods.HTBOTTOM : NativeMethods.HTCLIENT);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.Changed -= OnThemeChanged;
            _toolTips.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
        => ApplyPalette();

    private void TitleBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
    }

    private void TitleBarMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left)
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
        }
    }

    private int ScaleLogical(int value)
        => UiDrawing.Scale(this, value);
}

internal sealed class ManualTranslationForm : CherryForm
{
    private readonly Func<string, Task> _submit;
    private readonly TextBox _editor;
    private readonly Label _status;
    private readonly UiTextButton _translate;
    private bool _submitting;

    public ManualTranslationForm(TranslationProfile? profile, Func<string, Task> submit)
        : base("手动翻译", new Size(640, 430), new Size(480, 320), showPin: false, showOpacity: false)
    {
        _submit = submit;
        Name = "ManualTranslationForm";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = ThemeManager.Palette.Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = profile is null ? "输入要翻译的文字" : $"输入要翻译的文字  ·  {SafeProfileLabel(profile)}",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = ThemeManager.Palette.Text,
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);

        _editor = new TextBox
        {
            Name = "ManualSource",
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 10.5F),
            AcceptsReturn = true,
            AcceptsTab = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = ThemeManager.Palette.Card,
            ForeColor = ThemeManager.Palette.Text,
            PlaceholderText = "粘贴或输入文字，Ctrl+Enter 翻译"
        };
        _editor.KeyDown += EditorKeyDown;
        layout.Controls.Add(_editor, 0, 1);

        _status = new Label
        {
            Name = "Status",
            Text = "",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Margin = new Padding(0, 8, 0, 8)
        };
        layout.Controls.Add(_status, 0, 2);

        _translate = new UiTextButton("Translate", "翻译", "翻译", UiIcon.Translate)
        {
            Anchor = AnchorStyles.Right
        };
        _translate.Click += async (_, _) => await SubmitAsync();
        layout.Controls.Add(_translate, 0, 3);

        ContentPanel.Controls.Add(layout);
        Shown += (_, _) => _editor.Focus();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.SuppressKeyPress = true;
            }
        };
    }

    protected override void ApplyPalette()
    {
        base.ApplyPalette();
        _editor.BackColor = ThemeManager.Palette.Card;
        _editor.ForeColor = ThemeManager.Palette.Text;
        _status.ForeColor = ThemeManager.Palette.SecondaryText;
    }

    private static string SafeProfileLabel(TranslationProfile profile)
    {
        var provider = string.IsNullOrWhiteSpace(profile.ProviderName) ? "未命名提供商" : profile.ProviderName;
        var model = string.IsNullOrWhiteSpace(profile.ModelId) ? "未选择模型" : profile.ModelId;
        return $"{provider} / {model}";
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            _ = SubmitAsync();
        }
    }

    private async Task SubmitAsync()
    {
        if (_submitting)
        {
            return;
        }

        var text = _editor.Text.Trim();
        if (text.Length == 0)
        {
            _status.Text = "请输入要翻译的文字。";
            return;
        }

        _submitting = true;
        _translate.Enabled = false;
        _status.Text = "正在准备翻译…";
        try
        {
            await _submit(text);
            if (!IsDisposed)
            {
                _status.Text = "";
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                _status.Text = "已取消。";
            }
        }
        catch
        {
            if (!IsDisposed)
            {
                _status.Text = "无法开始翻译，请检查配置。";
            }
        }
        finally
        {
            _submitting = false;
            if (!IsDisposed)
            {
                _translate.Enabled = true;
            }
        }
    }
}

internal sealed class TranslationResultForm : CherryForm
{
    private readonly string _source;
    private readonly StreamingTranslateHandler _translate;
    private readonly Action<string, string>? _languageChanged;
    private TextBox _sourcePreview = null!;
    private readonly Panel _sourceCard;
    private Panel _sourceCopyPanel = null!;
    private readonly RichTextBox _translation;
    private readonly Label _status;
    private readonly UiTextButton _copy;
    private readonly UiTextButton _retry;
    private readonly UiTextButton _stopOrClose;
    private readonly UiTextButton _toggleOriginal;
    private readonly UiIconButton _languageSettings;
    private readonly ComboBox _targetLanguage;
    private ComboBox _preferredLanguage = null!;
    private ComboBox _alternateLanguage = null!;
    private readonly Label _sourceLanguage;
    private readonly Panel _settingsPanel;
    private readonly TrackBar _opacitySlider;
    private readonly Panel _opacityPopup;
    private FlowLayoutPanel _footer = null!;
    private CancellationTokenSource? _requestCancellation;
    private string _rawTranslation = string.Empty;
    private string _preferredLanguageCode;
    private string _targetLanguageCode;
    private string _alternateLanguageCode;
    private bool _closed;
    private bool _suppressLanguageEvents;
    private bool _languagePreferenceSaveFailed;

    public TranslationResultForm(string source, Func<CancellationToken, Task<string>> translate)
        : this(
            source,
            (_, _, cancellationToken) => translate(cancellationToken),
            "zh-cn",
            "en-us",
            null,
            null)
    {
    }

    internal TranslationResultForm(
        string source,
        StreamingTranslateHandler translate,
        string preferredTargetLanguage,
        string alternateLanguage,
        string? actualTargetLanguage = null,
        Action<string, string>? languageChanged = null)
        : base("翻译", new Size(500, 400), new Size(420, 400), showPin: true, showOpacity: true)
    {
        _source = source ?? string.Empty;
        _translate = translate ?? throw new ArgumentNullException(nameof(translate));
        _languageChanged = languageChanged;
        _preferredLanguageCode = LanguageCatalog.FindTarget(preferredTargetLanguage).Code;
        _alternateLanguageCode = LanguageCatalog.FindTarget(alternateLanguage).Code;
        var initialTarget = actualTargetLanguage;
        if (string.IsNullOrWhiteSpace(initialTarget))
        {
            var detected = LanguageCatalog.Detect(_source);
            initialTarget = LanguageCatalog.Matches(detected, _preferredLanguageCode)
                ? _alternateLanguageCode
                : _preferredLanguageCode;
        }

        _targetLanguageCode = LanguageCatalog.FindTarget(initialTarget).Code;
        Name = "TranslationResultForm";
        Text = "轻译 · 翻译结果";
        AccessibleName = "翻译结果";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = ThemeManager.Palette.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var languageRow = new FlowLayoutPanel
        {
            Name = "LanguageRow",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 44,
            MinimumSize = new Size(0, 44),
            WrapContents = true,
            AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = ThemeManager.Palette.Surface,
            Margin = new Padding(0, 0, 0, 8)
        };
        var resizingLanguageRow = false;
        languageRow.Resize += (_, _) =>
        {
            if (resizingLanguageRow || languageRow.ClientSize.Width <= 0)
            {
                return;
            }

            resizingLanguageRow = true;
            try
            {
                languageRow.PerformLayout();
                var bottom = languageRow.Controls.Cast<Control>()
                    .Where(control => control.Visible)
                    .Select(control => control.Bottom)
                    .DefaultIfEmpty(UiDrawing.Scale(languageRow, 44))
                    .Max();
                var desiredHeight = Math.Max(UiDrawing.Scale(languageRow, 44), bottom + UiDrawing.Scale(languageRow, 2));
                if (languageRow.Height != desiredHeight)
                {
                    languageRow.Height = desiredHeight;
                }
            }
            finally
            {
                resizingLanguageRow = false;
            }
        };
        _sourceLanguage = new Label
        {
            Name = "SourceLanguage",
            AutoSize = true,
            Text = LanguageCatalog.Find(LanguageCatalog.Detect(_source)).Name,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Padding = new Padding(8, 5, 8, 5),
            BackColor = ThemeManager.Palette.Muted,
            Margin = new Padding(0, 0, 4, 0)
        };
        languageRow.Controls.Add(_sourceLanguage);
        var arrow = new UiIconButton(UiIcon.ArrowRight, "LanguageDirection", "语言方向")
        {
            Size = new Size(24, 26),
            Enabled = false
        };
        languageRow.Controls.Add(arrow);

        _targetLanguage = CreateLanguageCombo("TargetLanguage", "目标语言", _targetLanguageCode);
        _settingsPanel = BuildLanguageSettingsPanel();
        _settingsPanel.Visible = false;
        _sourceCard = BuildSourceCard();
        _sourceCard.Visible = false;
        // Keep a native SourcePreview child available for accessibility and desktop inspection.
        _sourcePreview.CreateControl();

        _targetLanguage.SelectedIndexChanged += TargetLanguageChanged;
        languageRow.Controls.Add(_targetLanguage);
        _languageSettings = new UiIconButton(UiIcon.Settings, "LanguageSettings", "语言设置");
        _languageSettings.Click += (_, _) =>
        {
            _settingsPanel.Visible = !_settingsPanel.Visible;
            _languageSettings.Invalidate();
        };
        languageRow.Controls.Add(_languageSettings);
        _toggleOriginal = new UiTextButton("ToggleOriginal", "ToggleOriginal", "原文", UiIcon.ChevronDown)
        {
            Margin = new Padding(4, 0, 0, 0),
            AutoSize = true
        };
        _toggleOriginal.Click += (_, _) =>
        {
            _sourceCard.Visible = !_sourceCard.Visible;
            _sourcePreview.Visible = _sourceCard.Visible;
            _toggleOriginal.Text = _sourceCard.Visible ? "收起原文" : "原文";
            _sourceCard.Parent?.PerformLayout();
        };
        languageRow.Controls.Add(_toggleOriginal);
        layout.Controls.Add(languageRow, 0, 0);

        layout.Controls.Add(_settingsPanel, 0, 1);

        layout.Controls.Add(_sourceCard, 0, 2);

        _translation = new RichTextBox
        {
            Name = "TranslationContent",
            ReadOnly = true,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 10.5F),
            BorderStyle = BorderStyle.None,
            BackColor = ThemeManager.Palette.Surface,
            ForeColor = ThemeManager.Palette.Text,
            TabStop = true,
            Margin = new Padding(0, 8, 0, 8)
        };
        layout.Controls.Add(_translation, 0, 3);

        _status = new Label
        {
            Name = "Status",
            Text = "",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Margin = new Padding(0, 2, 0, 6)
        };
        layout.Controls.Add(_status, 0, 4);

        var footer = _footer = new UiFlowLayoutPanel
        {
            Name = "Footer",
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0)
        };
        var resizingFooter = false;
        footer.Layout += (_, _) =>
        {
            if (resizingFooter)
            {
                return;
            }

            var desiredHeight = footer.Controls.Cast<Control>()
                .Where(control => control.Visible)
                .Select(control => control.Bottom + control.Margin.Bottom + footer.Padding.Bottom)
                .DefaultIfEmpty(0)
                .Max();
            var availableHeight = footer.Parent is null
                ? desiredHeight
                : Math.Max(1, footer.Parent.ClientSize.Height - footer.Top);
            var nextHeight = Math.Min(desiredHeight, availableHeight);
            if (nextHeight <= 0)
            {
                return;
            }

            resizingFooter = true;
            try
            {
                if (footer.Height != nextHeight)
                {
                    footer.Height = nextHeight;
                }

                var childHeight = Math.Max(1, footer.ClientSize.Height - footer.Padding.Vertical);
                foreach (Control child in footer.Controls)
                {
                    var maximumChildHeight = Math.Max(1, childHeight - child.Margin.Vertical);
                    if (child.Height > maximumChildHeight)
                    {
                        child.Height = maximumChildHeight;
                    }
                }
            }
            finally
            {
                resizingFooter = false;
            }
        };
        _stopOrClose = new UiTextButton("StopOrClose", "StopOrClose", "Esc 关闭", UiIcon.Close);
        _stopOrClose.Click += (_, _) => StopOrClose();
        _retry = new UiTextButton("Regenerate", "Regenerate", "R 重新翻译", UiIcon.Refresh);
        _retry.Click += async (_, _) => await StartTranslationAsync();
        _copy = new UiTextButton("CopyTranslation", "CopyTranslation", "C 复制", UiIcon.Copy)
        {
            Enabled = false
        };
        _copy.Click += (_, _) => CopyTranslation();
        footer.Controls.Add(_stopOrClose);
        footer.Controls.Add(_retry);
        footer.Controls.Add(_copy);
        layout.Controls.Add(footer, 0, 5);

        _opacitySlider = new TrackBar
        {
            Name = "OpacitySlider",
            Minimum = 20,
            Maximum = 100,
            Value = 100,
            TickFrequency = 20,
            Orientation = Orientation.Vertical,
            Height = 100,
            Width = 34,
            BackColor = ThemeManager.Palette.Card
        };
        _opacitySlider.ValueChanged += (_, _) => Opacity = _opacitySlider.Value / 100d;
        _opacityPopup = new Panel
        {
            Name = "OpacityPopup",
            Size = new Size(48, 120),
            BackColor = ThemeManager.Palette.Card,
            Visible = false
        };
        _opacityPopup.Controls.Add(_opacitySlider);
        ContentPanel.Controls.Add(_opacityPopup);
        ContentPanel.Controls.Add(layout);
        _opacityPopup.BringToFront();

        _translation.TextChanged += (_, _) => _copy.Enabled = _rawTranslation.Length > 0;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        NormalizeFooterLayout();
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        NormalizeFooterLayout();
    }

    public bool IsRequestRunning => !_closed && _requestCancellation is { IsCancellationRequested: false };

    public string TargetLanguageCode => _targetLanguageCode;

    public string PreferredLanguageCode => _preferredLanguageCode;

    public string AlternateLanguageCode => _alternateLanguageCode;

    public TextBox SourcePreview => _sourcePreview;

    public RichTextBox TranslationContent => _translation;

    public void BeginTranslation()
        => _ = StartTranslationAsync();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closed = true;
        _requestCancellation?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void OnOpacityRequested()
    {
        _opacityPopup.Visible = !_opacityPopup.Visible;
        if (_opacityPopup.Visible)
        {
            _opacityPopup.Location = new Point(Math.Max(0, Width - _opacityPopup.Width - 12), TitleBar.Bottom + 4);
            _opacityPopup.BringToFront();
        }
    }

    protected override void ApplyPalette()
    {
        base.ApplyPalette();
        var palette = ThemeManager.Palette;
        _sourcePreview.BackColor = palette.Muted;
        _sourcePreview.ForeColor = palette.SecondaryText;
        _sourceCard.BackColor = palette.Muted;
        _sourceCopyPanel.BackColor = palette.Muted;
        _translation.BackColor = palette.Surface;
        _translation.ForeColor = palette.Text;
        _settingsPanel.BackColor = palette.Card;
        _opacityPopup.BackColor = palette.Card;
        _opacitySlider.BackColor = palette.Card;
        _sourceLanguage.BackColor = palette.Muted;
        _sourceLanguage.ForeColor = palette.SecondaryText;
        UiTheme.ApplyControls(ContentPanel);
        if (!string.IsNullOrWhiteSpace(_rawTranslation))
        {
            MarkdownRichTextRenderer.Render(_translation, _rawTranslation, palette);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (key == Keys.Escape && modifiers == Keys.None)
        {
            StopOrClose();
            return true;
        }

        if (modifiers == Keys.None && !IsEditingControlFocused())
        {
            if (key == Keys.R)
            {
                _ = StartTranslationAsync();
                return true;
            }

            if (key == Keys.C)
            {
                CopyTranslation();
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task StartTranslationAsync()
    {
        if (_closed)
        {
            return;
        }

        var request = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _requestCancellation, request);
        previous?.Cancel();
        _rawTranslation = string.Empty;
        _translation.Clear();
        _status.Text = _languagePreferenceSaveFailed
            ? "正在翻译… · 语言偏好保存失败"
            : "正在翻译…";
        _stopOrClose.Text = "Esc 停止";
        _retry.Enabled = false;
        _copy.Enabled = false;

        IProgress<string> progress = new FormProgress(this, request);

        try
        {
            var result = await _translate(_targetLanguageCode, progress, request.Token);
            if (!IsCurrent(request))
            {
                return;
            }

            SetTranslation(result.Trim());
            _status.Text = _languagePreferenceSaveFailed
                ? "翻译完成 · 语言偏好保存失败"
                : "翻译完成";
            _stopOrClose.Text = "Esc 关闭";
            _copy.Enabled = _rawTranslation.Length > 0;
            _retry.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentIdentity(request))
            {
                _status.Text = StatusWithPreferenceWarning(_rawTranslation.Length > 0 ? "已停止，保留当前结果" : "已停止");
                _stopOrClose.Text = "Esc 关闭";
                _copy.Enabled = _rawTranslation.Length > 0;
                _retry.Enabled = true;
            }
        }
        catch (TranslationException exception)
        {
            if (IsCurrentIdentity(request))
            {
                _status.Text = StatusWithPreferenceWarning(exception.Message);
                _stopOrClose.Text = "Esc 关闭";
                _retry.Enabled = true;
            }
        }
        catch (HttpRequestException)
        {
            if (IsCurrentIdentity(request))
            {
                _status.Text = StatusWithPreferenceWarning("翻译服务暂时不可用，请稍后重试。");
                _stopOrClose.Text = "Esc 关闭";
                _retry.Enabled = true;
            }
        }
        catch
        {
            if (IsCurrentIdentity(request))
            {
                _status.Text = StatusWithPreferenceWarning("翻译失败，请稍后重试。");
                _stopOrClose.Text = "Esc 关闭";
                _retry.Enabled = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, request))
            {
                _requestCancellation = null;
                if (!_closed && !IsDisposed && !Disposing)
                {
                    _retry.Enabled = true;
                }
            }

            request.Dispose();
        }
    }

    private bool IsCurrent(CancellationTokenSource request)
        => IsCurrentIdentity(request) && !request.IsCancellationRequested;

    private bool IsCurrentIdentity(CancellationTokenSource request)
        => !_closed && ReferenceEquals(_requestCancellation, request);

    private void ApplyProgress(CancellationTokenSource request, string partial)
    {
        if (IsCurrent(request) && !request.IsCancellationRequested)
        {
            SetTranslation(partial);
        }
    }

    private string StatusWithPreferenceWarning(string status)
        => _languagePreferenceSaveFailed && !status.Contains("语言偏好保存失败", StringComparison.Ordinal)
            ? $"{status} · 语言偏好保存失败"
            : status;

    private void SetTranslation(string value)
    {
        if (_closed)
        {
            return;
        }

        var followEnd = ShouldFollowTranslationEnd();
        _rawTranslation = value;
        MarkdownRichTextRenderer.Render(_translation, value, ThemeManager.Palette);
        _copy.Enabled = value.Length > 0;
        if (followEnd)
        {
            _translation.Select(_translation.TextLength, 0);
            _translation.ScrollToCaret();
        }
    }

    private void NormalizeFooterLayout()
    {
        if (_footer is null || _footer.Parent is null || _footer.IsDisposed)
        {
            return;
        }

        var availableHeight = _footer.Parent.ClientSize.Height - _footer.Top;
        if (availableHeight <= 0)
        {
            return;
        }

        if (_footer.Height > availableHeight)
        {
            _footer.Height = availableHeight;
        }

        var childHeight = Math.Max(1, _footer.ClientSize.Height - _footer.Padding.Vertical);
        foreach (Control child in _footer.Controls)
        {
            var maximumChildHeight = Math.Max(1, childHeight - child.Margin.Vertical);
            if (child.Height > maximumChildHeight)
            {
                child.Height = maximumChildHeight;
            }
        }
    }

    private bool ShouldFollowTranslationEnd()
    {
        if (_translation.SelectionLength > 0)
        {
            return false;
        }

        if (_translation.TextLength == 0 || !_translation.IsHandleCreated)
        {
            return true;
        }

        var firstLine = NativeMethods.SendMessage(
            _translation.Handle,
            NativeMethods.EM_GETFIRSTVISIBLELINE,
            IntPtr.Zero,
            IntPtr.Zero).ToInt32();
        var lastLine = _translation.GetLineFromCharIndex(_translation.TextLength);
        var visibleLines = Math.Max(1, _translation.ClientSize.Height / Math.Max(1, _translation.Font.Height));
        return firstLine + visibleLines >= lastLine;
    }

    private void StopOrClose()
    {
        var request = _requestCancellation;
        if (request is { IsCancellationRequested: false })
        {
            request.Cancel();
            _status.Text = StatusWithPreferenceWarning(_rawTranslation.Length > 0 ? "正在停止，已保留当前结果" : "正在停止…");
            _stopOrClose.Text = "Esc 关闭";
            return;
        }

        Close();
    }

    private void CopyTranslation()
    {
        if (string.IsNullOrWhiteSpace(_rawTranslation))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_rawTranslation);
            _status.Text = StatusWithPreferenceWarning("已复制译文");
        }
        catch
        {
            _status.Text = StatusWithPreferenceWarning("复制失败。");
        }
    }

    private void TargetLanguageChanged(object? sender, EventArgs e)
    {
        if (_suppressLanguageEvents || _targetLanguage.SelectedItem is not LanguageOption option)
        {
            return;
        }

        _targetLanguageCode = option.Code;
        _ = StartTranslationAsync();
    }

    private void PreferredLanguageChanged(object? sender, EventArgs e)
    {
        if (_suppressLanguageEvents || _preferredLanguage.SelectedItem is not LanguageOption option)
        {
            return;
        }

        _preferredLanguageCode = option.Code;
        SaveLanguagePreferences();
        SetActualTargetFromPreferences();
        _ = StartTranslationAsync();
    }

    private void AlternateLanguageChanged(object? sender, EventArgs e)
    {
        if (_suppressLanguageEvents || _alternateLanguage.SelectedItem is not LanguageOption option)
        {
            return;
        }

        _alternateLanguageCode = option.Code;
        SaveLanguagePreferences();
        SetActualTargetFromPreferences();
        _ = StartTranslationAsync();
    }

    private void SaveLanguagePreferences()
    {
        if (_languageChanged is null)
        {
            return;
        }

        try
        {
            _languageChanged(_preferredLanguageCode, _alternateLanguageCode);
            _languagePreferenceSaveFailed = false;
        }
        catch
        {
            _languagePreferenceSaveFailed = true;
            _status.Text = "语言偏好保存失败，当前翻译仍使用本次选择。";
        }
    }

    private void SetActualTargetFromPreferences()
    {
        var detected = LanguageCatalog.Detect(_source);
        var next = LanguageCatalog.Matches(detected, _preferredLanguageCode)
            ? _alternateLanguageCode
            : _preferredLanguageCode;
        _targetLanguageCode = LanguageCatalog.FindTarget(next).Code;
        _suppressLanguageEvents = true;
        try
        {
            SelectLanguageOption(_targetLanguage, _targetLanguageCode);
        }
        finally
        {
            _suppressLanguageEvents = false;
        }
    }

    private Panel BuildLanguageSettingsPanel()
    {
        var panel = new Panel
        {
            Name = "LanguageSettingsPanel",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = ThemeManager.Palette.Card,
            Padding = new Padding(8, 5, 8, 5),
            Margin = new Padding(0, 0, 0, 6)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = ThemeManager.Palette.Card,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "首选目标语言",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Margin = new Padding(0, 4, 0, 0)
        }, 0, 0);
        _preferredLanguage = CreateLanguageCombo("PreferredLanguage", "首选目标语言", _preferredLanguageCode);
        _preferredLanguage.Dock = DockStyle.Left;
        _preferredLanguage.SelectedIndexChanged += PreferredLanguageChanged;
        layout.Controls.Add(_preferredLanguage, 1, 0);
        layout.Controls.Add(new Label
        {
            Text = "备用语言",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Margin = new Padding(0, 4, 0, 0)
        }, 0, 1);
        _alternateLanguage = CreateLanguageCombo("AlternateLanguage", "备用语言", _alternateLanguageCode);
        _alternateLanguage.Dock = DockStyle.Left;
        _alternateLanguage.SelectedIndexChanged += AlternateLanguageChanged;
        layout.Controls.Add(_alternateLanguage, 1, 1);
        panel.Controls.Add(layout);

        // A ComboBox can report a larger native height than its assigned
        // Height (especially in a DPI-unaware process). Let each row account
        // for the control's preferred height and its default vertical margin,
        // then add the panel padding. This keeps both rows inside the card at
        // 96 DPI without relying on a guessed fixed height.
        var comboHeight = Math.Max(
            _preferredLanguage.GetPreferredSize(new Size(0, 0)).Height,
            _alternateLanguage.GetPreferredSize(new Size(0, 0)).Height);
        comboHeight = Math.Max(comboHeight,
            Math.Max(_preferredLanguage.Height, _alternateLanguage.Height));
        var rowMargin = Math.Max(
            _preferredLanguage.Margin.Vertical,
            _alternateLanguage.Margin.Vertical);
        var rowHeight = comboHeight + rowMargin;
        layout.MinimumSize = new Size(0, rowHeight * 2);
        panel.MinimumSize = new Size(0, layout.MinimumSize.Height + panel.Padding.Vertical);
        panel.Height = panel.MinimumSize.Height;
        return panel;
    }

    private Panel BuildSourceCard()
    {
        var panel = new Panel
        {
            Name = "SourcePreviewCard",
            Dock = DockStyle.Fill,
            Height = 64,
            MinimumSize = new Size(0, 64),
            AutoSize = true,
            BackColor = ThemeManager.Palette.Muted,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 4)
        };
        _sourcePreview = new TextBox
        {
            Name = "SourcePreview",
            Text = _source.Length > 12_000 ? _source[..12_000] : _source,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            BackColor = ThemeManager.Palette.Muted,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Font = new Font("Microsoft YaHei UI", 9F),
            AccessibleName = "SourcePreview"
        };
        var copy = new UiIconButton(UiIcon.Copy, "CopySource", "复制选区")
        {
            Dock = DockStyle.Top,
            Size = new Size(28, 26),
            Margin = new Padding(0)
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_source);
                _status.Text = "已复制原文";
            }
            catch
            {
                _status.Text = "复制失败。";
            }
        };
        _sourceCopyPanel = new Panel
        {
            Name = "SourceCopyPanel",
            Dock = DockStyle.Right,
            Width = 36,
            BackColor = ThemeManager.Palette.Muted,
            Padding = new Padding(0, 8, 8, 0),
            Margin = new Padding(0)
        };
        _sourceCopyPanel.Controls.Add(copy);
        panel.Controls.Add(_sourcePreview);
        panel.Controls.Add(_sourceCopyPanel);
        return panel;
    }

    private static ComboBox CreateLanguageCombo(string name, string accessibleName, string selectedCode)
    {
        var combo = new ComboBox
        {
            Name = name,
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.ComboBox,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FormattingEnabled = true,
            AutoSize = false,
            Width = 112,
            Height = 27,
            MinimumSize = new Size(112, 27),
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 27,
            BackColor = ThemeManager.Palette.Card,
            ForeColor = ThemeManager.Palette.Text,
        };
        var options = LanguageCatalog.TargetOptions.ToList();
        var selected = LanguageCatalog.FindTarget(selectedCode);
        if (!options.Any(option => string.Equals(option.Code, selected.Code, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(selected);
        }

        combo.Items.AddRange(options.Cast<object>().ToArray());
        combo.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= combo.Items.Count)
            {
                return;
            }

            var palette = ThemeManager.Palette;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? palette.AccentSurface : palette.Card);
            e.Graphics.FillRectangle(background, e.Bounds);
            var option = combo.Items[e.Index] as LanguageOption;
            var text = option?.Name ?? combo.GetItemText(combo.Items[e.Index]);
            TextRenderer.DrawText(
                e.Graphics,
                text,
                combo.Font,
                new Rectangle(e.Bounds.X + UiDrawing.Scale(combo, 8), e.Bounds.Y, Math.Max(1, e.Bounds.Width - UiDrawing.Scale(combo, 12)), e.Bounds.Height),
                palette.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        };
        combo.SelectedIndex = options.FindIndex(option =>
            string.Equals(option.Code, selected.Code, StringComparison.OrdinalIgnoreCase));
        return combo;
    }

    private static void SelectLanguageOption(ComboBox combo, string code)
    {
        var index = combo.Items.Cast<LanguageOption>().ToList().FindIndex(option =>
            string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            combo.SelectedIndex = index;
        }
    }

    private bool IsEditingControlFocused()
    {
        var focused = FindFocusedControl(this);
        return focused switch
        {
            ComboBox comboBox => comboBox.DroppedDown || comboBox.Focused,
            TextBoxBase textBox => !textBox.ReadOnly,
            _ => false
        };
    }

    private static Control? FindFocusedControl(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Focused)
            {
                return child;
            }

            if (child.ContainsFocus)
            {
                return FindFocusedControl(child);
            }
        }

        return parent.Focused ? parent : null;
    }

    private sealed class FormProgress : IProgress<string>
    {
        private readonly TranslationResultForm _owner;
        private readonly CancellationTokenSource _request;

        public FormProgress(TranslationResultForm owner, CancellationTokenSource request)
        {
            _owner = owner;
            _request = request;
        }

        public void Report(string? value)
        {
            if (value is null || _owner._closed || _owner.IsDisposed || _owner.Disposing || !_owner.IsHandleCreated)
            {
                return;
            }

            try
            {
                if (_owner.InvokeRequired)
                {
                    _owner.BeginInvoke(new Action(() => _owner.ApplyProgress(_request, value)));
                }
                else
                {
                    _owner.ApplyProgress(_request, value);
                }
            }
            catch (InvalidOperationException)
            {
                // The window may be closing between the handle checks and BeginInvoke.
            }
        }
    }
}

internal sealed class DiagnosticsForm : CherryForm
{
    public DiagnosticsForm(ProfileLoadResult result)
        : base("诊断", new Size(520, 300), new Size(420, 240), showPin: false, showOpacity: false)
    {
        Name = "DiagnosticsForm";
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            ColumnCount = 2,
            RowCount = 5,
            BackColor = ThemeManager.Palette.Surface
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, 0, "配置状态", result.Profile is null ? "未配置" : "已配置");
        AddRow(layout, 1, "提供商", Safe(result.Profile?.ProviderName));
        AddRow(layout, 2, "模型", Safe(result.Profile?.ModelId));
        AddRow(layout, 3, "来源", result.ImportedFromCherry ? "Cherry 配置" : result.LoadedFromStore ? "轻译配置" : "无");
        AddRow(layout, 4, "提示", result.Error ?? "未检测到错误。");
        ContentPanel.Controls.Add(layout);
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "未设置" : value;

    private static void AddRow(TableLayoutPanel layout, int row, string name, string value)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Margin = new Padding(0, 0, 8, 12)
        }, 0, row);
        layout.Controls.Add(new Label
        {
            Text = value,
            AutoSize = true,
            ForeColor = ThemeManager.Palette.Text,
            Margin = new Padding(0, 0, 0, 12)
        }, 1, row);
    }
}

internal sealed class ProfileSettingsForm : CherryForm
{
    private readonly Func<Task> _retryImport;
    private readonly Func<Task>? _useDeepSeek;

    public ProfileSettingsForm(string? error, Func<Task> retryImport, Func<Task>? useDeepSeek)
        : base("配置", new Size(560, 310), new Size(460, 240), showPin: false, showOpacity: false)
    {
        _retryImport = retryImport;
        _useDeepSeek = useDeepSeek;
        Name = "ProfileSettingsForm";
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = ThemeManager.Palette.Surface
        };
        layout.Controls.Add(new Label
        {
            Text = "轻译没有找到可用的 Cherry 模型配置。",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = ThemeManager.Palette.Text,
            Margin = new Padding(0, 0, 0, 10)
        });
        layout.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(error)
                ? "请确认 Cherry Studio 已配置模型，然后重新导入。"
                : $"{error}\r\n请确认 Cherry Studio 已配置模型，然后重新导入。",
            AutoSize = true,
            MaximumSize = new Size(490, 70),
            ForeColor = ThemeManager.Palette.SecondaryText,
            Margin = new Padding(0, 0, 0, 14)
        });

        var retry = new UiTextButton("Reimport", "重新导入 Cherry 配置", "重新导入 Cherry 配置", UiIcon.Refresh);
        retry.Click += async (_, _) => await RetryAsync();
        layout.Controls.Add(retry);

        if (useDeepSeek is not null)
        {
            var deepSeek = new UiTextButton("UseDeepSeek", "使用 Cherry 中的 DeepSeek 配置", "使用 Cherry 中的 DeepSeek 配置", UiIcon.Settings);
            deepSeek.Click += async (_, _) => await DeepSeekAsync();
            layout.Controls.Add(deepSeek);
        }

        layout.Controls.Add(new Label
        {
            Text = "轻译不会自动发送文本；翻译请求只在你点击按钮后发出。",
            AutoSize = true,
            ForeColor = ThemeManager.Palette.SecondaryText,
            Margin = new Padding(0, 16, 0, 0)
        });
        ContentPanel.Controls.Add(layout);
    }

    private async Task RetryAsync()
    {
        try
        {
            await _retryImport();
            if (!IsDisposed)
            {
                Close();
            }
        }
        catch
        {
            // Host updates its tray/status UI and keeps this actionable window open.
        }
    }

    private async Task DeepSeekAsync()
    {
        if (_useDeepSeek is null)
        {
            return;
        }

        try
        {
            await _useDeepSeek();
            if (!IsDisposed)
            {
                Close();
            }
        }
        catch
        {
            // Host updates its tray/status UI and keeps this actionable window open.
        }
    }
}

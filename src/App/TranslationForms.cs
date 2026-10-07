using CherryTranslate.Core;
using System.Net.Http;

namespace CherryTranslate.App;

internal sealed class ManualTranslationForm : Form
{
    private readonly Func<string, Task> _submit;
    private readonly TextBox _editor;
    private readonly Label _status;
    private readonly Button _translate;
    private bool _submitting;

    public ManualTranslationForm(TranslationProfile? profile, Func<string, Task> submit)
    {
        _submit = submit;
        Text = "轻译 · 手动翻译";
        Width = 640;
        Height = 430;
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.White;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = profile is null
                ? "输入要翻译的文字"
                : $"输入要翻译的文字  ·  {SafeProfileLabel(profile)}",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10)
        };
        layout.Controls.Add(heading, 0, 0);

        _editor = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 11F),
            AcceptsReturn = true,
            AcceptsTab = true,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "粘贴或输入文字，Ctrl+Enter 翻译"
        };
        _editor.KeyDown += EditorKeyDown;
        layout.Controls.Add(_editor, 0, 1);

        _status = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 100, 100),
            Margin = new Padding(0, 8, 0, 8)
        };
        layout.Controls.Add(_status, 0, 2);

        _translate = new Button
        {
            Text = "翻译",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Padding = new Padding(18, 6, 18, 6),
            UseVisualStyleBackColor = true
        };
        _translate.Click += async (_, _) => await SubmitAsync();
        layout.Controls.Add(_translate, 0, 3);

        var hint = new Label
        {
            Text = "Ctrl+Enter 翻译 · Esc 关闭",
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Color.FromArgb(125, 125, 125),
            Margin = new Padding(0, 12, 0, 0)
        };
        layout.Controls.Add(hint, 0, 4);

        Controls.Add(layout);
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _editor.KeyDown -= EditorKeyDown;
    }
}

internal sealed class TranslationResultForm : Form
{
    private readonly string _source;
    private readonly Func<CancellationToken, Task<string>> _translate;
    private readonly TextBox _sourcePreview;
    private readonly RichTextBox _translation;
    private readonly Label _status;
    private readonly Button _copy;
    private readonly Button _retry;
    private readonly Button _cancel;
    private CancellationTokenSource? _requestCancellation = new();
    private bool _closed;

    public TranslationResultForm(
        string source,
        Func<CancellationToken, Task<string>> translate)
    {
        _source = source;
        _translate = translate;
        Text = "轻译 · 翻译结果";
        Width = 700;
        Height = 520;
        MinimumSize = new Size(520, 380);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        BackColor = Color.White;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "原文",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
        }, 0, 0);

        _sourcePreview = new TextBox
        {
            Text = source.Length > 4000 ? source[..4000] + "…" : source,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(248, 248, 248),
            BorderStyle = BorderStyle.FixedSingle
        };
        layout.Controls.Add(_sourcePreview, 0, 1);

        layout.Controls.Add(new Label
        {
            Text = "译文",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 4)
        }, 0, 2);

        _translation = new RichTextBox
        {
            ReadOnly = true,
            DetectUrls = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 11F),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White
        };
        layout.Controls.Add(_translation, 0, 3);

        _status = new Label
        {
            Text = "",
            AutoSize = true,
            MaximumSize = new Size(640, 48),
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(100, 100, 100),
            Margin = new Padding(0, 8, 0, 8)
        };
        layout.Controls.Add(_status, 0, 4);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        _copy = new Button { Text = "复制译文", AutoSize = true, Enabled = false };
        _copy.Click += (_, _) => CopyTranslation();
        _retry = new Button { Text = "重试", AutoSize = true };
        _retry.Click += async (_, _) => await StartTranslationAsync();
        _cancel = new Button { Text = "取消", AutoSize = true };
        _cancel.Click += (_, _) => Close();
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_retry);
        actions.Controls.Add(_copy);
        layout.Controls.Add(actions, 0, 5);

        layout.Controls.Add(new Label
        {
            Text = "选中文字可复制 · Esc 关闭",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(125, 125, 125),
            Margin = new Padding(0, 12, 0, 0)
        }, 0, 6);

        Controls.Add(layout);
        KeyDown += ResultKeyDown;
        FormClosing += (_, _) =>
        {
            _closed = true;
            var request = Interlocked.Exchange(ref _requestCancellation, null);
            if (request is not null)
            {
                request.Cancel();
                request.Dispose();
            }
        };
    }

    public bool IsRequestRunning => !_closed && _requestCancellation is { IsCancellationRequested: false };

    public void BeginTranslation()
        => _ = StartTranslationAsync();

    private async Task StartTranslationAsync()
    {
        if (_closed)
        {
            return;
        }

        var requestCancellation = new CancellationTokenSource();
        var previousRequest = _requestCancellation;
        _requestCancellation = requestCancellation;
        if (previousRequest is not null)
        {
            previousRequest.Cancel();
            previousRequest.Dispose();
        }
        _translation.Clear();
        _status.Text = "正在翻译…";
        _copy.Enabled = false;
        _retry.Enabled = false;
        _cancel.Text = "取消";

        try
        {
            var result = await _translate(requestCancellation.Token);
            if (!IsCurrentRequest(requestCancellation))
            {
                return;
            }
            _translation.Text = result.Trim();
            _status.Text = "翻译完成";
            _copy.Enabled = _translation.TextLength > 0;
            _cancel.Text = "关闭";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentRequest(requestCancellation))
            {
                _status.Text = "已取消";
                _cancel.Text = "关闭";
            }
        }
        catch (TranslationException exception)
        {
            if (IsCurrentRequest(requestCancellation))
            {
                _status.Text = exception.Message;
                _retry.Enabled = true;
                _cancel.Text = "关闭";
            }
        }
        catch (HttpRequestException)
        {
            if (IsCurrentRequest(requestCancellation))
            {
                _status.Text = "翻译服务暂时不可用，请稍后重试。";
                _retry.Enabled = true;
                _cancel.Text = "关闭";
            }
        }
        catch
        {
            if (IsCurrentRequest(requestCancellation))
            {
                _status.Text = "翻译失败，请稍后重试。";
                _retry.Enabled = true;
                _cancel.Text = "关闭";
            }
        }
        finally
        {
            if (IsCurrentRequest(requestCancellation))
            {
                _retry.Enabled = true;
            }
        }
    }

    private bool IsCurrentRequest(CancellationTokenSource request)
        => !_closed &&
           ReferenceEquals(_requestCancellation, request) &&
           !request.IsCancellationRequested;

    private void CopyTranslation()
    {
        try
        {
            if (_translation.TextLength > 0)
            {
                Clipboard.SetText(_translation.Text);
                _status.Text = "已复制译文";
            }
        }
        catch
        {
            _status.Text = "复制失败。";
        }
    }

    private void ResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.SuppressKeyPress = true;
        }
    }
}

internal sealed class DiagnosticsForm : Form
{
    public DiagnosticsForm(ProfileLoadResult result)
    {
        Text = "轻译 · 诊断";
        Width = 520;
        Height = 300;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = 5,
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, 0, "配置状态", result.Profile is null ? "未配置" : "已配置");
        AddRow(layout, 1, "提供商", Safe(result.Profile?.ProviderName));
        AddRow(layout, 2, "模型", Safe(result.Profile?.ModelId));
        AddRow(layout, 3, "来源", result.ImportedFromCherry ? "Cherry 配置" : result.LoadedFromStore ? "轻译配置" : "无");
        AddRow(layout, 4, "提示", result.Error ?? "未检测到错误。");
        Controls.Add(layout);
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "未设置" : value;

    private static void AddRow(TableLayoutPanel layout, int row, string name, string value)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(0, 0, 8, 12) }, 0, row);
        layout.Controls.Add(new Label { Text = value, AutoSize = true, Margin = new Padding(0, 0, 0, 12) }, 1, row);
    }
}

internal sealed class ProfileSettingsForm : Form
{
    private readonly Func<Task> _retryImport;
    private readonly Func<Task>? _useDeepSeek;

    public ProfileSettingsForm(string? error, Func<Task> retryImport, Func<Task>? useDeepSeek)
    {
        _retryImport = retryImport;
        _useDeepSeek = useDeepSeek;
        Text = "轻译 · 配置";
        Width = 560;
        Height = useDeepSeek is null ? 250 : 310;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        layout.Controls.Add(new Label
        {
            Text = "轻译没有找到可用的 Cherry 模型配置。",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10)
        });
        layout.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(error)
                ? "请确认 Cherry Studio 已配置模型，然后重新导入。"
                : $"{error}\r\n请确认 Cherry Studio 已配置模型，然后重新导入。",
            AutoSize = true,
            MaximumSize = new Size(490, 70),
            Margin = new Padding(0, 0, 0, 14)
        });

        var retry = new Button { Text = "重新导入 Cherry 配置", AutoSize = true };
        retry.Click += async (_, _) => await RetryAsync();
        layout.Controls.Add(retry);

        if (useDeepSeek is not null)
        {
            var deepSeek = new Button { Text = "使用 Cherry 中的 DeepSeek 配置", AutoSize = true };
            deepSeek.Click += async (_, _) => await DeepSeekAsync();
            layout.Controls.Add(deepSeek);
        }

        layout.Controls.Add(new Label
        {
            Text = "轻译不会自动发送文本；翻译请求只在你点击按钮后发出。",
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 110, 110),
            Margin = new Padding(0, 16, 0, 0)
        });
        Controls.Add(layout);
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

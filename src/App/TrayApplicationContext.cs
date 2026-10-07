using CherryTranslate.Core;

namespace CherryTranslate.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ProfileRuntime _profiles = new();
    private readonly NotifyIcon _tray;
    private readonly FloatingTranslateButton _floatingButton;
    private readonly ClipboardCaptureService _clipboard;
    private readonly SelectionQueryService _queryService;
    private readonly GlobalMouseHook? _mouseHook;
    private readonly SelectionController? _selection;
    private readonly HotkeyWindow? _hotkey;
    private readonly ProfileLoadResult _initialProfileResult;
    private readonly bool _demoMode;
    private readonly string? _previewKind;
    private readonly System.Windows.Forms.Timer? _exitTimer;
    private ManualTranslationForm? _manualForm;
    private TranslationResultForm? _resultForm;
    private ProfileSettingsForm? _settingsForm;
    private bool _shutDown;
    private bool _cleaned;
    private EventHandler? _themeChangedHandler;

    public TrayApplicationContext(bool demoMode = false, int? exitAfterSeconds = null, string? previewKind = null)
    {
        _demoMode = demoMode;
        _previewKind = previewKind;
        _initialProfileResult = demoMode
            ? new ProfileLoadResult(
                new TranslationProfile("Demo", "demo", "https://demo.invalid", string.Empty),
                null,
                false,
                true)
            : _profiles.LoadOrImport();
        if (_initialProfileResult.Profile is not null && _profiles.Current is null)
        {
            _profiles.SetCurrent(_initialProfileResult.Profile);
        }

        _floatingButton = new FloatingTranslateButton();
        _floatingButton.CreateControl();
        var ownWindow = _floatingButton.Handle;
        _clipboard = new ClipboardCaptureService(ownWindow);

        _queryService = new SelectionQueryService(ownWindow);
        GlobalMouseHook? hook = null;
        if (string.IsNullOrWhiteSpace(_previewKind))
        {
            try
            {
                hook = new GlobalMouseHook();
            }
            catch
            {
                // The app remains useful through the tray manual editor and explicit hotkey.
                DemoTrace.Write("startup", "hook_failure");
            }
        }
        _mouseHook = hook;
        if (hook is not null)
        {
            _selection = new SelectionController(
                SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext(),
                hook,
                _queryService,
                _floatingButton);
            _selection.TranslateRequested += probe => _ = TranslateSelectionAsync(probe);
        }

        if (string.IsNullOrWhiteSpace(_previewKind))
        {
            try
            {
                _hotkey = new HotkeyWindow(OnHotkeyPressed);
            }
            catch
            {
                _hotkey = null;
                DemoTrace.Write("startup", "hotkey_failure");
            }
        }

        _tray = BuildTrayIcon();
        Application.ApplicationExit += OnApplicationExit;
        DemoTrace.Write("startup", $"ready;hook={(_mouseHook is not null)};hotkey={(_hotkey?.IsRegistered ?? false)};demo={_demoMode}");

        if (exitAfterSeconds is not null)
        {
            _exitTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Clamp(exitAfterSeconds.Value, 1, 60) * 1000
            };
            _exitTimer.Tick += (_, _) => Shutdown();
            _exitTimer.Start();
        }

        if (string.Equals(_previewKind, "result", StringComparison.OrdinalIgnoreCase))
        {
            _floatingButton.BeginInvoke((Action)(() => OpenResult(
                "The meeting starts at nine tomorrow morning. Please bring the project notes.",
                PreviewAnchor())));
        }
        else if (string.Equals(_previewKind, "manual", StringComparison.OrdinalIgnoreCase))
        {
            _floatingButton.BeginInvoke((Action)OpenManualEditor);
        }
        else if (_initialProfileResult.Profile is not null && _initialProfileResult.ImportedFromCherry)
        {
            _floatingButton.BeginInvoke((Action)OpenManualEditor);
        }
        else if (_initialProfileResult.Profile is null)
        {
            _floatingButton.BeginInvoke((Action)ShowProfileSettings);
        }
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("暂停划词监听");
        pause.Click += (_, _) =>
        {
            if (_selection is null)
            {
                pause.Text = "划词监听不可用";
                return;
            }
            _selection.SetPaused(!_selection.IsPaused);
            pause.Text = _selection.IsPaused ? "恢复划词监听" : "暂停划词监听";
        };
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());

        var manual = new ToolStripMenuItem("手动翻译");
        manual.Click += (_, _) => OpenManualEditor();
        menu.Items.Add(manual);

        var reimport = new ToolStripMenuItem("重新导入 Cherry 配置");
        reimport.Enabled = !_demoMode;
        reimport.Click += async (_, _) => await ReimportAsync();
        menu.Items.Add(reimport);

        var deepSeek = new ToolStripMenuItem("使用 Cherry 中的 DeepSeek 配置");
        deepSeek.Enabled = !_demoMode;
        deepSeek.Click += async (_, _) => await ReimportAsync("deepseek::deepseek-v4-flash");
        menu.Items.Add(deepSeek);

        var diagnostics = new ToolStripMenuItem("诊断");
        diagnostics.Click += (_, _) => ShowDiagnostics();
        menu.Items.Add(diagnostics);
        var theme = new ToolStripMenuItem("主题");
        var followTheme = AddThemeItem(theme, "跟随系统", ThemeMode.Follow);
        var lightTheme = AddThemeItem(theme, "浅色", ThemeMode.Light);
        var darkTheme = AddThemeItem(theme, "深色", ThemeMode.Dark);
        void RefreshThemeChecks()
        {
            followTheme.Checked = ThemeManager.Mode == ThemeMode.Follow;
            lightTheme.Checked = ThemeManager.Mode == ThemeMode.Light;
            darkTheme.Checked = ThemeManager.Mode == ThemeMode.Dark;
        }
        RefreshThemeChecks();
        _themeChangedHandler = (_, _) => RefreshThemeChecks();
        ThemeManager.Changed += _themeChangedHandler;
        menu.Items.Add(theme);
        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);

        var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = BuildTrayText(_initialProfileResult.Profile),
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => OpenManualEditor();
        return tray;
    }

    private ToolStripMenuItem AddThemeItem(ToolStripMenuItem parent, string text, ThemeMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => ThemeManager.SetMode(mode, persist: !_demoMode);
        parent.DropDownItems.Add(item);
        return item;
    }

    private static string BuildTrayText(TranslationProfile? profile)
    {
        if (profile is null)
        {
            return "轻译 LightTranslate（未配置）";
        }

        var provider = string.IsNullOrWhiteSpace(profile.ProviderName) ? "已配置" : profile.ProviderName;
        var model = string.IsNullOrWhiteSpace(profile.ModelId) ? "" : $" / {profile.ModelId}";
        var text = $"轻译 LightTranslate · {provider}{model}";
        return text.Length <= 63 ? text : text[..60] + "…";
    }

    private void OpenManualEditor()
    {
        if (_manualForm is not null && !_manualForm.IsDisposed)
        {
            if (!_manualForm.Visible)
            {
                _manualForm.Show();
            }
            _manualForm.Activate();
            return;
        }

        _manualForm = new ManualTranslationForm(_profiles.Current, SubmitManualAsync);
        var manualForm = _manualForm;
        manualForm.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_manualForm, manualForm))
            {
                _manualForm = null;
            }
        };
        _manualForm.Show();
    }

    private Task SubmitManualAsync(string text)
    {
        if (_profiles.Current is null)
        {
            ShowProfileSettings();
            return Task.CompletedTask;
        }

        OpenResult(text);
        return Task.CompletedTask;
    }

    private async Task TranslateSelectionAsync(SelectionProbe probe)
    {
        try
        {
            var profile = _profiles.Current;
            if (profile is null)
            {
                ShowProfileSettings();
                return;
            }

            var text = probe.Text?.Trim();
            DemoTrace.Write("translate_request", $"clicked_length={text?.Length ?? 0};fallback={string.IsNullOrWhiteSpace(text)}");
            if (string.IsNullOrWhiteSpace(text))
            {
                var capture = await _clipboard.CopySelectionAsync(probe.TargetWindow, CancellationToken.None);
                if (!capture.Success)
                {
                    ShowSafeMessage(capture.Error ?? "没有读取到文本选区。", "轻译");
                    return;
                }
                text = capture.Text;
                DemoTrace.Write("translate_capture", $"success={capture.Success};length={text?.Length ?? 0}");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowSafeMessage("选区为空。", "轻译");
                return;
            }

            OpenResult(text, probe.ScreenPoint);
            DemoTrace.Write("translate_open", $"length={text.Length}");
        }
        catch (OperationCanceledException)
        {
            // Closing the app or starting another request cancels the pending capture.
        }
        catch
        {
            if (!_shutDown)
            {
                ShowSafeMessage("读取选区失败。", "轻译");
            }
        }
    }

    private void OpenResult(string text, Point? anchor = null)
    {
        var profile = _profiles.Current;
        if (profile is null)
        {
            ShowProfileSettings();
            return;
        }

        _resultForm?.Close();
        _resultForm = new TranslationResultForm(
            text,
            (targetLanguageOverride, progress, cancellationToken) =>
                TranslateStreamingAsync(profile, text, targetLanguageOverride, progress, cancellationToken),
            profile.TargetLanguage,
            profile.AlternateLanguage,
            ResolveActualTarget(profile, text),
            _demoMode ? null : (primary, alternate) => _profiles.UpdateLanguages(primary, alternate));
        var resultForm = _resultForm;
        resultForm.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_resultForm, resultForm))
            {
                _resultForm = null;
            }
        };
        _resultForm.Show();
        if (anchor is { } screenPoint)
        {
            var workArea = Screen.FromPoint(screenPoint).WorkingArea;
            if (workArea.Width > 0 && workArea.Height > 0 &&
                (_resultForm.Width > workArea.Width || _resultForm.Height > workArea.Height))
            {
                // A remote session can report a work area smaller than the normal
                // minimum. Let the content scroll rather than producing an invalid
                // upper clamp or placing the window outside the work area.
                var savedMinimum = _resultForm.MinimumSize;
                _resultForm.MinimumSize = Size.Empty;
                _resultForm.Size = new Size(
                    Math.Min(_resultForm.Width, workArea.Width),
                    Math.Min(_resultForm.Height, workArea.Height));
                _resultForm.MinimumSize = new Size(
                    Math.Min(savedMinimum.Width, workArea.Width),
                    Math.Min(savedMinimum.Height, workArea.Height));
            }
            var maxX = Math.Max(workArea.Left, workArea.Right - _resultForm.Width);
            var maxY = Math.Max(workArea.Top, workArea.Bottom - _resultForm.Height);
            var x = Math.Clamp(screenPoint.X + 12, workArea.Left, maxX);
            var y = Math.Clamp(screenPoint.Y + 16, workArea.Top, maxY);
            _resultForm.Location = new Point(x, y);
        }
        _resultForm.BeginTranslation();
    }

    private async Task<string> TranslateStreamingAsync(
        TranslationProfile profile,
        string text,
        string? targetLanguageOverride,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (_demoMode)
        {
            const string demo = "会议明天上午九点开始。请带上项目笔记。\n\n**流式输出**与语言切换会更新当前窗口。";
            progress?.Report(demo[..Math.Min(12, demo.Length)]);
            await Task.Delay(40, cancellationToken);
            progress?.Report(demo);
            return demo;
        }

        using var client = new TranslationClient();
        return await client.TranslateStreamingAsync(
            profile,
            text,
            progress,
            targetLanguageOverride,
            cancellationToken);
    }

    private static string ResolveActualTarget(TranslationProfile profile, string text)
    {
        try
        {
            return TranslationClient.ResolveTargetLanguage(profile, text);
        }
        catch
        {
            return string.IsNullOrWhiteSpace(profile.TargetLanguage) ? "zh-cn" : profile.TargetLanguage;
        }
    }

    private static Point PreviewAnchor()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Point(area.Left + 80, area.Top + 120);
    }

    private async void OnHotkeyPressed()
    {
        var target = NativeMethods.GetForegroundWindow();
        if (target == IntPtr.Zero || IsOurProcessWindow(target))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // RegisterHotKey fires while Ctrl+Alt are still held. Wait before injecting
            // Ctrl+C so the target receives a plain copy command.
            var releaseDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (AreModifierKeysDown() && !cancellation.IsCancellationRequested)
            {
                await Task.Delay(25, cancellation.Token);
            }

            if (AreModifierKeysDown() || DateTime.UtcNow > releaseDeadline)
            {
                ShowSafeMessage("请先松开快捷键修饰键。", "轻译");
                return;
            }

            if (_shutDown || NativeMethods.GetForegroundWindow() != target)
            {
                return;
            }

            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                ShowSafeMessage("无法确认当前选区位置。", "轻译");
                return;
            }

            var safeProbe = await _queryService.QueryAsync(
                target,
                new Point(cursor.X, cursor.Y),
                cancellation.Token);
            if (safeProbe is null || safeProbe.Ignored || !safeProbe.CanUseCopyFallback)
            {
                ShowSafeMessage("当前焦点不适合安全复制，未读取文本。", "轻译");
                return;
            }

            var capture = await _clipboard.CopySelectionAsync(target, cancellation.Token);
            if (!capture.Success || string.IsNullOrWhiteSpace(capture.Text))
            {
                ShowSafeMessage(capture.Error ?? "没有读取到文本选区。", "轻译");
                return;
            }

            if (_profiles.Current is null)
            {
                ShowProfileSettings();
                return;
            }

            OpenResult(capture.Text);
        }
        catch (OperationCanceledException)
        {
            ShowSafeMessage("快捷键复制已取消。", "轻译");
        }
        catch
        {
            ShowSafeMessage("快捷键复制失败。", "轻译");
        }
    }

    private static bool AreModifierKeysDown()
        => (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_CONTROL) & 0x8000) != 0 ||
           (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0 ||
           (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0 ||
           (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
           (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

    private bool IsOurProcessWindow(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var targetProcess);
            NativeMethods.GetWindowThreadProcessId(_floatingButton.Handle, out var ownProcess);
            return targetProcess != 0 && targetProcess == ownProcess;
        }
        catch
        {
            return false;
        }
    }

    private void ShowDiagnostics()
    {
        using var diagnostics = new DiagnosticsForm(new ProfileLoadResult(
            _profiles.Current,
            null,
            false,
            _profiles.Current is not null));
        diagnostics.ShowDialog();
    }

    private void ShowProfileSettings()
    {
        if (_settingsForm is not null && !_settingsForm.IsDisposed)
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new ProfileSettingsForm(
            _initialProfileResult.Error,
            () => ReimportAsync(),
            () => ReimportAsync("deepseek::deepseek-v4-flash"));
        var settingsForm = _settingsForm;
        settingsForm.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsForm, settingsForm))
            {
                _settingsForm = null;
            }
        };
        _settingsForm.Show();
    }

    private Task ReimportAsync(string? modelOverride = null)
    {
        if (_demoMode)
        {
            return Task.CompletedTask;
        }

        var result = _profiles.Reimport(modelOverride);
        if (result.Profile is not null && result.Error is null)
        {
            _tray.Text = BuildTrayText(result.Profile);
            _settingsForm?.Close();
            OpenManualEditor();
        }
        else if (!string.IsNullOrWhiteSpace(result.Error))
        {
            ShowSafeMessage(result.Error, "轻译配置");
        }
        return Task.CompletedTask;
    }

    private static void ShowSafeMessage(string message, string title)
    {
        try
        {
            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch
        {
            // The app may be shutting down; no UI is required in that case.
        }
    }

    private void Shutdown()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;
        DemoTrace.Write("shutdown", "requested");
        _resultForm?.Close();
        _manualForm?.Close();
        _settingsForm?.Close();
        Application.ExitThread();
    }

    private void OnApplicationExit(object? sender, EventArgs e)
    {
        if (_shutDown)
        {
            Cleanup();
            return;
        }

        _shutDown = true;
        Cleanup();
    }

    private void Cleanup()
    {
        if (_cleaned)
        {
            return;
        }

        _cleaned = true;
        Application.ApplicationExit -= OnApplicationExit;
        if (_themeChangedHandler is not null)
        {
            ThemeManager.Changed -= _themeChangedHandler;
            _themeChangedHandler = null;
        }
        if (_exitTimer is not null)
        {
            _exitTimer.Stop();
            _exitTimer.Dispose();
        }
        _selection?.Dispose();
        _mouseHook?.Dispose();
        _hotkey?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (!_floatingButton.IsDisposed)
        {
            _floatingButton.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cleanup();
        }
        base.Dispose(disposing);
    }
}

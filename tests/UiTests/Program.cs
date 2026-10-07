using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;

namespace CherryTranslate.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var dpiUnaware = args.Any(argument =>
            string.Equals(argument, "--dpi-unaware", StringComparison.OrdinalIgnoreCase));
        // Keep an exception from an async WinForms callback from opening a
        // modal .NET error dialog and hanging a headless or CI run.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.SetHighDpiMode(dpiUnaware ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            using var messageHost = UiMessageHost.Create();
            var resultFormType = UiDiscovery.FindTranslationFormType();
            var artifactDirectory = Path.Combine(
                Environment.CurrentDirectory,
                ".artifacts",
                dpiUnaware ? "ui-dpi-unaware" : "ui");
            Directory.CreateDirectory(artifactDirectory);

            var failures = new List<(string Name, Exception Error)>();
            RunTest("button paint backgrounds and state transitions", failures, ButtonPaintingTests.Run);
            RunTest("original and language controls", failures,
                () => TestOriginalAndLanguageControls(resultFormType, artifactDirectory));
            RunTest("markdown content", failures, () => TestMarkdownContentPreservation(resultFormType));
            RunTest("progress cancellation and retry", failures,
                () => TestProgressCancellationAndRetry(resultFormType));
            RunTest("stale result isolation", failures, () => TestStaleResultIsolation(resultFormType));
            RunTest("scaled and narrow layouts", failures,
                () => TestScaledAndNarrowLayouts(resultFormType, artifactDirectory));
            RunTest("theme screenshots", failures,
                () => TestThemeScreenshots(resultFormType, artifactDirectory));

            if (failures.Count > 0)
            {
                foreach (var (name, error) in failures)
                {
                    Console.Error.WriteLine($"[{name}] {error}");
                }

                return 1;
            }

            Console.WriteLine("UI tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void RunTest(
        string name,
        ICollection<(string Name, Exception Error)> failures,
        Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures.Add((name, exception));
        }
    }

    private static void TestOriginalAndLanguageControls(Type formType, string artifactDirectory)
    {
        const string source = "你好，世界";
        var probe = new TranslationProbe();
        probe.Enqueue(_ => Task.FromResult("狐狸跳过了懒狗。"));

        using var form = UiDiscovery.CreateTranslationForm(formType, source, probe);
        form.Show();
        UiDiscovery.StartIfIdle(form, probe);
        PumpUntil(() => probe.Calls >= 1, "translation request");

        Require(!UiDiscovery.HasVisibleText(form, source),
            "the original selection should be collapsed on first render");

        var originalToggle = UiDiscovery.FindControl(form, "ToggleOriginal", "原文", "显示");
        Require(originalToggle is not null, "the original-text expand button is missing");
        UiDiscovery.Click(originalToggle!);
        PumpFor(TimeSpan.FromMilliseconds(80));
        Require(UiDiscovery.HasVisibleText(form, source),
            "expanding the original selection did not show the selected text");

        UiDiscovery.Click(originalToggle!);
        PumpFor(TimeSpan.FromMilliseconds(80));
        Require(!UiDiscovery.HasVisibleText(form, source),
            "collapsing the original selection did not hide the selected text");

        var copy = UiDiscovery.FindControl(form, "CopyTranslation", "复制");
        Require(copy is not null, "the translation copy button is missing");
        PumpUntil(() => UiDiscovery.HasVisibleText(form, "狐狸跳过了懒狗。"), "translation result");
        UiDiscovery.SaveScreenshot(form, Path.Combine(artifactDirectory, "ui-result-100-default.png"));
        Require(copy!.Enabled, "copy should be enabled after a result is available");
        Require(probe.TranslationTargets.FirstOrDefault() == "en-us",
            "Chinese input should initially translate to the alternate language en-us");
        Require(UiDiscovery.GetStringProperty(form, "TargetLanguageCode") == "en-us",
            "Chinese input should use the alternate language as the actual target");
        Require(UiDiscovery.GetStringProperty(form, "PreferredLanguageCode") == "zh-cn",
            "Chinese input should keep zh-cn as the preferred target language");

        var sourcePreview = UiDiscovery.FindControl(form, "SourcePreview") as TextBox;
        var sourceCopy = UiDiscovery.FindControl(form, "CopySource", "复制选区");
        UiDiscovery.Click(originalToggle!);
        PumpFor(TimeSpan.FromMilliseconds(80));
        Require(sourcePreview is not null && sourcePreview.Visible,
            "expanding the original selection did not expose the source preview");
        Require(sourceCopy is not null && sourceCopy.Visible,
            "expanding the original selection did not expose the source copy button");
        var sourcePreviewBounds = sourcePreview!.RectangleToScreen(sourcePreview.ClientRectangle);
        var sourceCopyBounds = sourceCopy!.RectangleToScreen(sourceCopy.ClientRectangle);
        Require(!sourcePreviewBounds.IntersectsWith(sourceCopyBounds),
            "the source copy button overlaps the source preview text area");
        UiDiscovery.Click(originalToggle!);
        PumpFor(TimeSpan.FromMilliseconds(80));

        var translation = UiDiscovery.FindRichTextBox(form, "TranslationContent", "译文");
        Require(translation is not null && translation.ReadOnly,
            "the rendered translation should be a read-only selectable control");
        var callsBeforeShortcut = probe.Calls;
        translation!.Focus();
        translation.SelectionStart = 0;
        translation.SelectionLength = Math.Min(translation.TextLength, 3);
        probe.Enqueue(_ => Task.FromResult("R shortcut result"));
        Require(UiDiscovery.SendFormShortcut(form, Keys.R),
            "the R shortcut should trigger a retry while the read-only translation has focus");
        PumpUntil(() => probe.Calls > callsBeforeShortcut, "R shortcut retry");
        PumpUntil(() => UiDiscovery.HasVisibleText(form, "R shortcut result"), "R shortcut result");

        var selectionStart = translation.SelectionStart;
        var selectionLength = translation.SelectionLength;
        Require(!UiDiscovery.SendFormShortcut(form, Keys.Control | Keys.C),
            "Ctrl+C should be delegated to the read-only control's normal selection handling");
        Require(translation.SelectionStart == selectionStart && translation.SelectionLength == selectionLength,
            "Ctrl+C changed the read-only translation selection");

        var retry = UiDiscovery.FindControl(form, "Regenerate", "retry", "重试", "重新");
        Require(retry is not null && retry.Enabled,
            "Regenerate should be enabled after a successful translation");
        var callsBeforeRegenerate = probe.Calls;
        probe.Enqueue(_ => Task.FromResult("regenerated result"));
        UiDiscovery.Click(retry!);
        PumpUntil(() => probe.Calls > callsBeforeRegenerate, "Regenerate request");
        PumpUntil(() => UiDiscovery.HasVisibleText(form, "regenerated result"), "Regenerate result");
        Require(retry!.Enabled, "Regenerate should remain enabled after a successful retry");

        var callsBeforeSecondRegenerate = probe.Calls;
        probe.Enqueue(_ => Task.FromResult("second regenerated result"));
        UiDiscovery.Click(retry!);
        PumpUntil(() => probe.Calls > callsBeforeSecondRegenerate, "second Regenerate request");
        PumpUntil(() => UiDiscovery.HasVisibleText(form, "second regenerated result"), "second Regenerate result");

        var pin = UiDiscovery.FindControl(form, "Pin", "置顶");
        Require(pin is not null, "the pin button is missing");
        var topMostBefore = form.TopMost;
        UiDiscovery.Click(pin!);
        PumpFor(TimeSpan.FromMilliseconds(40));
        Require(form.TopMost != topMostBefore, "pin did not change the window topmost state");
        UiDiscovery.Click(pin!);
        PumpFor(TimeSpan.FromMilliseconds(40));
        Require(form.TopMost == topMostBefore, "pin did not restore the window topmost state");

        var opacityButton = UiDiscovery.FindControl(form, "Opacity", "透明度");
        Require(opacityButton is not null, "the opacity button is missing");
        UiDiscovery.Click(opacityButton!);
        PumpFor(TimeSpan.FromMilliseconds(40));
        var opacitySlider = UiDiscovery.FindTrackBar(form, "OpacitySlider", "透明度");
        Require(opacitySlider is not null, "opening opacity did not expose a slider");
        Require(opacitySlider!.Minimum == 20 && opacitySlider.Maximum == 100,
            "opacity slider range must be 20% to 100%");
        opacitySlider.Value = 20;
        Require(form.Opacity is >= 0.19 and <= 0.21, "20% opacity was not applied to the window");
        opacitySlider.Value = 100;
        Require(form.Opacity is >= 0.99 and <= 1.01, "100% opacity was not applied to the window");

        var language = UiDiscovery.FindComboBox(form, "TargetLanguage", "目标语言");
        Require(language is not null, "the target-language selector is missing");
        Require(UiDiscovery.GetSelectedLanguageCode(language!) == "en-us",
            "the visible target-language selector should show the actual en-us target for Chinese input");
        var callbackCountBeforeTarget = probe.CallbackCount;
        var callsBeforeTarget = probe.Calls;
        var originalIndex = language!.SelectedIndex;
        var alternateIndex = Enumerable.Range(0, language.Items.Count)
            .FirstOrDefault(index => index != originalIndex);
        Require(language.Items.Count >= 2 && alternateIndex != originalIndex,
            "the target-language selector does not expose a second option");
        language.SelectedIndex = alternateIndex;
        var selectedTargetCode = UiDiscovery.GetSelectedLanguageCode(language);
        PumpUntil(() => probe.Calls > callsBeforeTarget, "temporary target-language request");
        Require(probe.TranslationTargets.LastOrDefault() == selectedTargetCode,
            "the target-language selector did not control the next request target");
        Require(probe.CallbackCount == callbackCountBeforeTarget,
            "a temporary target-language selection should not persist the preferred language");
        Require(UiDiscovery.GetStringProperty(form, "PreferredLanguageCode") == "zh-cn",
            "temporary target-language selection changed the preferred language");

        var settings = UiDiscovery.FindControl(form, "LanguageSettings", "语言设置");
        Require(settings is not null, "the language settings button is missing");
        UiDiscovery.Click(settings!);
        PumpFor(TimeSpan.FromMilliseconds(40));
        var alternateLanguage = UiDiscovery.FindComboBox(form, "AlternateLanguage", "备用语言");
        Require(alternateLanguage is not null && alternateLanguage.Visible,
            "opening language settings did not show the alternate language selector");
        var preferredLanguage = UiDiscovery.FindComboBox(form, "PreferredLanguage", "首选语言", "偏好目标");
        Require(preferredLanguage is not null && preferredLanguage.Visible,
            "language settings should expose a preferred target selector");
        Require(UiDiscovery.GetSelectedLanguageCode(preferredLanguage!) == "zh-cn",
            "the visible preferred-language selector should show zh-cn");
        Require(UiDiscovery.GetSelectedLanguageCode(alternateLanguage!) == "en-us",
            "the visible alternate-language selector should show en-us");
        var callbackCountBeforeAlternate = probe.CallbackCount;
        var callsBeforeAlternate = probe.Calls;
        alternateLanguage!.SelectedIndex = (alternateLanguage.SelectedIndex + 1) % alternateLanguage.Items.Count;
        PumpUntil(() => probe.Calls > callsBeforeAlternate, "alternate-language request");
        Require(probe.CallbackCount > callbackCountBeforeAlternate,
            "changing the alternate language did not reach the language callback");

        var callbackCountBeforePreferred = probe.CallbackCount;
        preferredLanguage!.SelectedIndex = (preferredLanguage.SelectedIndex + 1) % preferredLanguage.Items.Count;
        PumpFor(TimeSpan.FromMilliseconds(80));
        Require(probe.CallbackCount > callbackCountBeforePreferred,
            "changing the preferred target did not reach the language callback");

        UiDiscovery.SaveScreenshot(form, Path.Combine(artifactDirectory, "ui-result-100.png"));
        form.Close();
        PumpFor(TimeSpan.FromMilliseconds(50));
    }

    private static void TestMarkdownContentPreservation(Type formType)
    {
        const string markdown = "# 标题\r\nemoji 😀 扩展汉字 𠀀 slash \\ braces {ok}";
        var probe = new TranslationProbe();
        probe.Enqueue(_ => Task.FromResult(markdown));
        using var form = UiDiscovery.CreateTranslationForm(formType, "markdown source", probe);
        form.Show();
        UiDiscovery.StartIfIdle(form, probe);
        PumpUntil(() => UiDiscovery.HasVisibleText(form, "标题"), "markdown result");

        var translation = UiDiscovery.FindRichTextBox(form, "TranslationContent", "译文");
        Require(translation is not null, "the translation content control is missing");
        Require(translation!.Text.Contains("😀", StringComparison.Ordinal),
            "emoji was not preserved by the markdown renderer");
        Require(translation.Text.Contains("𠀀", StringComparison.Ordinal),
            "an extension-plane Han character was not preserved by the markdown renderer");
        Require(translation.Text.Contains("\\", StringComparison.Ordinal),
            "a backslash was interpreted as an RTF control sequence");
        Require(translation.Text.Contains("{ok}", StringComparison.Ordinal),
            "braces were interpreted as an RTF control sequence");
        form.Close();
    }

    private static void TestProgressCancellationAndRetry(Type formType)
    {
        const string source = "A pending translation should be cancellable.";
        var cancellationProbe = new TranslationProbe();
        cancellationProbe.EnqueueProgress("中途流式片段");
        cancellationProbe.Enqueue(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        });

        using (var form = UiDiscovery.CreateTranslationForm(formType, source, cancellationProbe))
        {
            form.Show();
            UiDiscovery.StartIfIdle(form, cancellationProbe);
            PumpUntil(() => cancellationProbe.Calls >= 1, "pending translation request");
            var pendingTranslation = UiDiscovery.FindRichTextBox(form, "TranslationContent", "译文");
            Require(pendingTranslation is not null, "the pending translation content control is missing");
            PumpUntil(() => pendingTranslation!.Text.Contains("中途流式片段", StringComparison.Ordinal),
                "streaming partial result");

            var cancel = UiDiscovery.FindControl(form, "cancel", "取消", "stop", "停止", "pause", "暂停");
            Require(cancel is not null, "the translation cancel control is missing");
            Require(UiDiscovery.SendFormShortcut(form, Keys.Escape),
                "Escape should stop an in-flight translation");
            PumpUntil(() => form.IsDisposed || !UiDiscovery.IsRequestRunning(form),
                "translation cancellation");
            Require(!UiDiscovery.HasVisibleError(form),
                "normal cancellation should not render an error message");
            if (!form.IsDisposed)
            {
                Require(UiDiscovery.SendFormShortcut(form, Keys.Escape),
                    "Escape should close the result window after a request has stopped");
                PumpUntil(() => form.IsDisposed || !form.Visible,
                    "closing the stopped translation window");
            }
        }

        var retryProbe = new TranslationProbe();
        retryProbe.Enqueue(_ => Task.FromException<string>(new InvalidOperationException("synthetic failure")));
        retryProbe.Enqueue(_ => Task.FromResult("retry result"));
        using var retryForm = UiDiscovery.CreateTranslationForm(formType, "retry this text", retryProbe);
        retryForm.Show();
        UiDiscovery.StartIfIdle(retryForm, retryProbe);
        PumpUntil(() => retryProbe.Calls >= 1 && UiDiscovery.HasVisibleError(retryForm), "translation failure");

        var retry = UiDiscovery.FindControl(retryForm, "retry", "重试", "regenerate", "重新");
        Require(retry is not null, "a failed translation should expose a retry control");
        Require(retry!.Enabled, "the retry control should be enabled after a failed translation");
        UiDiscovery.Click(retry);
        PumpUntil(() => retryProbe.Calls >= 2, "retry request");
        PumpUntil(() => UiDiscovery.HasVisibleText(retryForm, "retry result"), "retry result");
        Require(!UiDiscovery.HasVisibleError(retryForm), "a successful retry left the error state visible");
    }

    private static void TestStaleResultIsolation(Type formType)
    {
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new TranslationProbe();
        probe.EnqueueProgress("旧请求流式片段");
        probe.EnqueueProgress("新请求流式片段");
        probe.Enqueue(_ => first.Task);
        probe.Enqueue(_ => Task.FromResult("new result"));

        var form = UiDiscovery.CreateTranslationForm(formType, "stale result test", probe);
        Exception? bodyFailure = null;
        try
        {
            form.Show();
            UiDiscovery.StartIfIdle(form, probe);
            PumpUntil(() => probe.Calls >= 1, "first stale-result request");
            var translation = UiDiscovery.FindRichTextBox(form, "TranslationContent", "译文");
            Require(translation is not null, "the stale-result translation content control is missing");
            PumpUntil(() => translation!.Text.Contains("旧请求流式片段", StringComparison.Ordinal),
                "first streaming partial result");

            Require(UiDiscovery.BeginTranslation(form),
                "the translation form does not expose a restart operation");
            PumpUntil(() => probe.Calls >= 2, "second stale-result request");
            first.TrySetResult("old result");
            PumpUntil(() => UiDiscovery.HasVisibleText(form, "new result"), "current translation result");
            PumpFor(TimeSpan.FromMilliseconds(100));
            Require(!UiDiscovery.HasVisibleText(form, "old result"),
                "an older translation result overwrote the current request");
            Require(!UiDiscovery.HasVisibleText(form, "旧请求流式片段"),
                "an older streaming progress fragment remained after a new request completed");
            PumpUntil(() => !UiDiscovery.IsRequestRunning(form), "stale-result requests settled");
            PumpFor(TimeSpan.FromMilliseconds(100));
        }
        catch (Exception exception)
        {
            bodyFailure = exception;
            Console.Error.WriteLine($"[stale result isolation body] {exception}");
        }
        finally
        {
            try
            {
                if (!form.IsDisposed)
                {
                    form.Close();
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[stale result isolation close] {exception}");
                bodyFailure ??= exception;
            }

            try
            {
                if (!form.IsDisposed)
                {
                    form.Dispose();
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[stale result isolation dispose] {exception}");
                bodyFailure ??= exception;
            }
        }

        if (bodyFailure is not null)
        {
            ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        }
    }

    private static void TestScaledAndNarrowLayouts(Type formType, string artifactDirectory)
    {
        var layoutFailures = new List<string>();
        var resizeFactors = new[] { 1f, 1.5f, 2f };
        foreach (var resizeFactor in resizeFactors)
        {
            var probe = new TranslationProbe();
            probe.Enqueue(_ => Task.FromResult("长文本布局检查结果\r\n第二行结果"));
            using var form = UiDiscovery.CreateTranslationForm(
                formType,
                "这是用于检查窗口在不同缩放比例下是否仍然可读的较长原文。\r\nThe original text remains selectable.",
                probe);
            form.Show();
            try
            {
                UiDiscovery.StartIfIdle(form, probe);
                var baseClientSize = form.ClientSize;
                if (resizeFactor != 1f)
                {
                    // Resize the window to exercise layout at larger work areas.
                    // The process DPI remains the real Windows DPI context.
                    form.ClientSize = new Size(
                        (int)Math.Round(baseClientSize.Width * resizeFactor),
                        (int)Math.Round(baseClientSize.Height * resizeFactor));
                    form.PerformLayout();
                }

                PumpUntil(() => UiDiscovery.HasVisibleText(form, "长文本布局检查结果"),
                    $"{resizeFactor:P0} resized layout result");
                UiDiscovery.AssertVisibleBounds(
                    form,
                    resizeFactor == 1f
                        ? $"actual DPI {form.DeviceDpi} base layout"
                        : $"{resizeFactor:P0} resized layout at actual DPI {form.DeviceDpi}");
            }
            catch (Exception exception)
            {
                layoutFailures.Add($"{resizeFactor:P0} resized layout: {exception.Message}");
            }
            finally
            {
                try
                {
                    UiDiscovery.SaveScreenshot(
                        form,
                        Path.Combine(artifactDirectory, $"ui-result-resize-{(int)(resizeFactor * 100)}.png"));
                    if (resizeFactor == 1f)
                    {
                        UiDiscovery.SaveScreenshot(
                            form,
                            Path.Combine(artifactDirectory, $"ui-result-dpi-{form.DeviceDpi}.png"));
                    }
                }
                catch (Exception exception)
                {
                    layoutFailures.Add($"{resizeFactor:P0} screenshot: {exception.Message}");
                }

                if (!form.IsDisposed)
                {
                    form.Close();
                }
            }
        }

        var narrowProbe = new TranslationProbe();
        narrowProbe.Enqueue(_ => Task.FromResult("窄窗口结果"));
        using var narrow = UiDiscovery.CreateTranslationForm(
            formType,
            "窄窗口布局检查原文",
            narrowProbe);
        narrow.Show();
        try
        {
            UiDiscovery.StartIfIdle(narrow, narrowProbe);
            var minimum = narrow.MinimumSize;
            narrow.ClientSize = new Size(
                Math.Max(minimum.Width > 0 ? minimum.Width : 420, 420),
                Math.Max(minimum.Height > 0 ? minimum.Height : 280, 280));
            narrow.PerformLayout();
            PumpUntil(() => UiDiscovery.HasVisibleText(narrow, "窄窗口结果"), "narrow layout result");

            var narrowSettings = UiDiscovery.FindControl(narrow, "LanguageSettings", "语言设置");
            Require(narrowSettings is not null, "narrow layout lost the language settings button");
            UiDiscovery.Click(narrowSettings!);
            PumpFor(TimeSpan.FromMilliseconds(60));
            var narrowOriginal = UiDiscovery.FindControl(narrow, "ToggleOriginal", "原文", "显示");
            Require(narrowOriginal is not null, "narrow layout lost the original toggle");
            UiDiscovery.Click(narrowOriginal!);
            PumpFor(TimeSpan.FromMilliseconds(60));
            var sourceCard = UiDiscovery.FindControl(narrow, "SourcePreviewCard");
            var settingsPanel = UiDiscovery.FindControl(narrow, "LanguageSettingsPanel");
            Require(sourceCard is not null && settingsPanel is not null
                && sourceCard.Visible && settingsPanel.Visible,
                "narrow layout could not show the original and language settings panels together");
            Require(!sourceCard!.Bounds.IntersectsWith(settingsPanel!.Bounds),
                "expanded original and language settings panels overlap at the minimum size");
            UiDiscovery.AssertVisibleBounds(narrow, "narrow");
        }
        catch (Exception exception)
        {
            layoutFailures.Add($"narrow: {exception.Message}");
        }
        finally
        {
            try
            {
                UiDiscovery.SaveScreenshot(narrow, Path.Combine(artifactDirectory, "ui-result-narrow-expanded.png"));
            }
            catch (Exception exception)
            {
                layoutFailures.Add($"narrow screenshot: {exception.Message}");
            }
        }

        if (layoutFailures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, layoutFailures));
        }
    }

    private static void TestThemeScreenshots(Type formType, string artifactDirectory)
    {
        var themeFailures = new List<string>();
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                UiDiscovery.SetTheme(theme);
                var probe = new TranslationProbe();
                probe.Enqueue(_ => Task.FromResult("会议明天上午九点开始。请带上项目笔记。"));
                var form = UiDiscovery.CreateTranslationForm(
                    formType,
                    "The meeting starts at nine tomorrow morning. Please bring the project notes.",
                    probe);
                try
                {
                    form.Show();
                    UiDiscovery.StartIfIdle(form, probe);
                    PumpUntil(() => UiDiscovery.HasVisibleText(form, "会议明天上午九点开始。请带上项目笔记。"), $"{theme} theme result");
                    UiDiscovery.AssertVisibleBounds(form, $"{theme} theme");
                }
                catch (Exception exception)
                {
                    themeFailures.Add($"{theme}: {exception.Message}");
                }
                finally
                {
                    try
                    {
                        UiDiscovery.SaveScreenshot(
                            form,
                            Path.Combine(artifactDirectory, $"ui-{theme.ToLowerInvariant()}.png"));
                    }
                    catch (Exception exception)
                    {
                        themeFailures.Add($"{theme} screenshot: {exception.Message}");
                    }

                    try
                    {
                        if (!form.IsDisposed)
                        {
                            form.Close();
                        }
                    }
                    catch (Exception exception)
                    {
                        themeFailures.Add($"{theme} close: {exception.Message}");
                    }
                }
            }
        }
        finally
        {
            UiDiscovery.SetTheme("Follow");
        }

        if (themeFailures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, themeFailures));
        }
    }

    private static void PumpUntil(Func<bool> condition, string phase)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromSeconds(5).TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Application.DoEvents();
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException($"Timed out during {phase}.");
    }

    private static void PumpFor(TimeSpan duration)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class TranslationProbe
{
    private readonly ConcurrentQueue<Func<CancellationToken, Task<string>>> _responses = new();
    private readonly ConcurrentQueue<string> _progressMessages = new();
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public int CallbackCount { get; private set; }

    public List<string> SeenLanguageValues { get; } = new();

    public List<string> TranslationTargets { get; } = new();

    public void Enqueue(Func<CancellationToken, Task<string>> response)
        => _responses.Enqueue(response);

    public void EnqueueProgress(string message)
        => _progressMessages.Enqueue(message);

    public object? InvokeDelegate(Type returnType, object?[] arguments)
    {
        var hasCancellation = arguments.Any(argument => argument is CancellationToken);
        var hasText = arguments.Any(argument => argument is string);
        foreach (var argument in arguments)
        {
            var value = argument switch
            {
                string text => text,
                null => string.Empty,
                _ => ReadLanguageValue(argument)
            };
            if (!string.IsNullOrWhiteSpace(value) && !SeenLanguageValues.Contains(value, StringComparer.Ordinal))
            {
                SeenLanguageValues.Add(value);
            }
        }

        var isTaskOfString = returnType == typeof(Task<string>)
            || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
        if (hasCancellation || (hasText && isTaskOfString))
        {
            var progress = arguments.OfType<IProgress<string>>().FirstOrDefault();
            var target = arguments.OfType<string>().FirstOrDefault();
            return TranslateAsync(arguments.OfType<CancellationToken>().FirstOrDefault(), progress, target);
        }

        CallbackCount++;
        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            return Activator.CreateInstance(returnType, GetDefault(returnType.GetGenericArguments()[0]));
        }

        return returnType == typeof(void) ? null : GetDefault(returnType);
    }

    private async Task<string> TranslateAsync(
        CancellationToken cancellationToken,
        IProgress<string>? progress,
        string? target)
    {
        var call = Interlocked.Increment(ref _calls);
        if (!string.IsNullOrWhiteSpace(target))
        {
            TranslationTargets.Add(target);
        }

        if (_progressMessages.TryDequeue(out var progressMessage))
        {
            progress?.Report(progressMessage);
        }

        if (!_responses.TryDequeue(out var response))
        {
            return $"translation-{call}";
        }

        return await response(cancellationToken).ConfigureAwait(true);
    }

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static string ReadLanguageValue(object argument)
    {
        var type = argument.GetType();
        foreach (var propertyName in new[] { "Code", "Name", "Value", "Text", "langCode" })
        {
            var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property?.GetValue(argument) is { } value)
            {
                return value.ToString() ?? string.Empty;
            }
        }

        return argument.ToString() ?? string.Empty;
    }
}

internal static class UiDiscovery
{
    private static readonly BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] ProgressWords =
    {
        "翻译", "检测", "准备", "处理中", "取消", "停止", "暂停", "loading", "translat"
    };

    private static readonly string[] ErrorWords =
    {
        "错误", "失败", "error", "failed", "exception"
    };

    public static Type FindTranslationFormType()
    {
        var appAssembly = Assembly.Load("LightTranslate");
        var candidates = appAssembly.GetTypes()
            .Where(type => typeof(Form).IsAssignableFrom(type))
            .Where(type => type.Name.Contains("Translation", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("Selection", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("Result", StringComparison.OrdinalIgnoreCase))
            .Where(HasSupportedConstructor)
            .OrderByDescending(type => type.Name.Contains("Result", StringComparison.OrdinalIgnoreCase))
            .ThenBy(type => type.Name)
            .ToArray();
        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find a translation result Form in the App assembly.");
    }

    public static Form CreateTranslationForm(Type formType, string source, TranslationProbe probe)
    {
        var constructor = formType.GetConstructors(AllInstance)
            .Where(constructor => constructor.IsPublic || constructor.IsAssembly || constructor.IsPrivate)
            .Where(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)))
            .Where(constructor => constructor.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)))
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .FirstOrDefault();
        if (constructor is null)
        {
            throw new InvalidOperationException($"No testable constructor was found on {formType.FullName}.");
        }

        var arguments = constructor.GetParameters()
            .Select(parameter => BuildArgument(parameter, source, probe))
            .ToArray();
        try
        {
            return (Form)constructor.Invoke(arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public static bool BeginTranslation(Form form)
    {
        var method = form.GetType().GetMethods(AllInstance)
            .Where(candidate => candidate.GetParameters().Length == 0)
            .Where(candidate => candidate.Name is "BeginTranslation" or "StartTranslation" or "Translate")
            .OrderBy(candidate => candidate.Name == "BeginTranslation" ? 0 : 1)
            .FirstOrDefault();
        if (method is null)
        {
            return false;
        }

        try
        {
            _ = method.Invoke(form, null);
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            return false;
        }
    }

    public static bool IsRequestRunning(Form form)
    {
        var property = form.GetType().GetProperty("IsRequestRunning", AllInstance);
        return property?.GetValue(form) as bool? ?? HasProgressIndicator(form);
    }

    public static void StartIfIdle(Form form, TranslationProbe probe)
    {
        EnsureUiContext();
        if (probe.Calls == 0 && !BeginTranslation(form))
        {
            throw new InvalidOperationException("The translation form did not start automatically and has no start operation.");
        }
    }

    public static void EnsureUiContext()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
    }

    public static Control? FindControl(Form form, params string[] tokens)
        => Enumerate(form)
            .Where(control => control is not Form)
            .OrderByDescending(control => control.Visible)
            .ThenByDescending(control => tokens.Any(token =>
                string.Equals(control.Name, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(control.AccessibleName, token, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(control => control.TabIndex)
            .FirstOrDefault(control => Matches(control, tokens));

    public static ComboBox? FindComboBox(Form form, params string[] tokens)
        => Enumerate(form).OfType<ComboBox>().FirstOrDefault(control => Matches(control, tokens))
           ?? Enumerate(form).OfType<ComboBox>().FirstOrDefault();

    public static string? GetStringProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName, AllInstance)?.GetValue(instance) as string;

    public static string GetSelectedLanguageCode(ComboBox combo)
    {
        var selected = combo.SelectedItem;
        if (selected is null)
        {
            return combo.SelectedValue?.ToString() ?? string.Empty;
        }

        var code = selected.GetType().GetProperty("Code", AllInstance)?.GetValue(selected)?.ToString();
        return code ?? combo.SelectedValue?.ToString() ?? selected.ToString() ?? string.Empty;
    }

    public static void SetTheme(string modeName)
    {
        var assembly = Assembly.Load("LightTranslate");
        var manager = assembly.GetType("CherryTranslate.App.ThemeManager", throwOnError: true)!;
        var modeType = assembly.GetType("CherryTranslate.App.ThemeMode", throwOnError: true)!;
        var mode = Enum.Parse(modeType, modeName, ignoreCase: true);
        var setMode = manager.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "SetMode")
            .Where(method => method.GetParameters().Length == 2)
            .FirstOrDefault();
        if (setMode is null)
        {
            throw new InvalidOperationException("ThemeManager.SetMode(mode, persist) is required for isolated UI tests.");
        }

        setMode.Invoke(null, new[] { mode, (object)false });
    }

    public static RichTextBox? FindRichTextBox(Form form, params string[] tokens)
        => Enumerate(form).OfType<RichTextBox>().FirstOrDefault(control => Matches(control, tokens))
           ?? Enumerate(form).OfType<RichTextBox>().FirstOrDefault();

    public static TrackBar? FindTrackBar(Form form, params string[] tokens)
        => Enumerate(form).OfType<TrackBar>().FirstOrDefault(control => Matches(control, tokens))
           ?? Enumerate(form).OfType<TrackBar>().FirstOrDefault();

    public static bool HasVisibleText(Control root, string text)
        => Enumerate(root).Any(control => control.Visible
            && !string.IsNullOrEmpty(control.Text)
            && control.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

    public static bool HasProgressIndicator(Form form)
        => Enumerate(form).Any(control => control.Visible && control is ProgressBar)
            || Enumerate(form).Any(control => control.Visible && ProgressWords.Any(word =>
                control.Text.Contains(word, StringComparison.OrdinalIgnoreCase)));

    public static bool HasVisibleError(Form form)
        => Enumerate(form).Any(control => control.Visible && ErrorWords.Any(word =>
            control.Text.Contains(word, StringComparison.OrdinalIgnoreCase)));

    public static bool SendFormShortcut(Form form, Keys keys)
    {
        var method = form.GetType().GetMethod("ProcessCmdKey", AllInstance);
        if (method is null)
        {
            return false;
        }

        var message = new Message();
        var result = method.Invoke(form, new object[] { message, keys });
        return result is bool handled && handled;
    }

    public static void Click(Control control)
    {
        if (control is Button button)
        {
            button.PerformClick();
            return;
        }

        var performClick = control.GetType().GetMethod("PerformClick", AllInstance, null, Type.EmptyTypes, null);
        if (performClick is not null)
        {
            performClick.Invoke(control, null);
            return;
        }

        throw new InvalidOperationException($"Control {control.Name} is not clickable.");
    }

    public static void SaveScreenshot(Form form, string path)
    {
        var size = new Size(Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height));
        using var bitmap = new Bitmap(size.Width, size.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, ImageFormat.Png);
    }

    public static void AssertVisibleBounds(Form form, string layout)
    {
        foreach (var control in Enumerate(form).Where(control => control.Visible && control.Parent is not null))
        {
            var parent = control.Parent!;
            var bounds = control.Bounds;
            var client = parent.ClientRectangle;
            if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > client.Right + 1 || bounds.Bottom > client.Bottom + 1)
            {
                throw new InvalidOperationException(
                    $"{layout} layout placed {control.GetType().Name} '{control.Name}' outside its parent bounds: {bounds} / {client}.");
            }

            if (control is Label label && !string.IsNullOrWhiteSpace(label.Text) && control.Width <= 0)
            {
                throw new InvalidOperationException($"{layout} layout hid label '{label.Text}'.");
            }
        }
    }

    private static bool HasSupportedConstructor(Type type)
        => type.GetConstructors(AllInstance).Any(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(string))
            && constructor.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));

    private static object? BuildArgument(ParameterInfo parameter, string source, TranslationProbe probe)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string))
        {
            if (parameter.Name?.Contains("actual", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Let the form resolve the first target from the detected source language.
                return null;
            }

            if (parameter.Name?.Contains("target", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "zh-cn";
            }

            if (parameter.Name?.Contains("alternate", StringComparison.OrdinalIgnoreCase) == true
                || parameter.Name?.Contains("alter", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "en-us";
            }

            return source;
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return BuildDelegate(type, probe);
        }

        if (type == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }

        return GetDefault(type);
    }

    private static Delegate BuildDelegate(Type delegateType, TranslationProbe probe)
    {
        var invoke = delegateType.GetMethod("Invoke", AllInstance)
            ?? throw new InvalidOperationException($"Delegate {delegateType.FullName} has no Invoke method.");
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var arguments = Expression.NewArrayInit(
            typeof(object),
            parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
        var callback = typeof(TranslationProbe).GetMethod(nameof(TranslationProbe.InvokeDelegate), AllInstance)
            ?? throw new MissingMethodException(nameof(TranslationProbe.InvokeDelegate));
        var invocation = Expression.Call(
            Expression.Constant(probe),
            callback,
            Expression.Constant(invoke.ReturnType),
            arguments);

        Expression body;
        if (invoke.ReturnType == typeof(void))
        {
            body = Expression.Block(invocation, Expression.Empty());
        }
        else
        {
            body = Expression.Convert(invocation, invoke.ReturnType);
        }

        return Expression.Lambda(delegateType, body, parameters).Compile();
    }

    private static IEnumerable<Control> Enumerate(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (var descendant in Enumerate(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool Matches(Control control, IReadOnlyCollection<string> tokens)
    {
        var values = new[]
        {
            control.Name,
            control.AccessibleName,
            control.Text,
            control.GetType().Name
        };
        return tokens.Any(token => values.Any(value => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;
}

internal sealed class UiMessageHost : IDisposable
{
    private readonly Form _host;

    private UiMessageHost()
    {
        _host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
            Size = new Size(1, 1),
            Opacity = 0
        };
        _host.Show();
        _host.Hide();
        UiDiscovery.EnsureUiContext();
    }

    public static UiMessageHost Create() => new();

    public void Dispose()
    {
        try
        {
            if (!_host.IsDisposed)
            {
                _host.Close();
            }
        }
        catch
        {
            // The host exists only to keep WinForms callbacks on the UI thread.
        }

        try
        {
            if (!_host.IsDisposed)
            {
                _host.Dispose();
            }
        }
        catch
        {
            // Do not let test-only message-host cleanup mask test results.
        }
    }
}

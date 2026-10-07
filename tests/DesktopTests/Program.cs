using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CherryTranslate.DesktopTests;

internal readonly record struct DragSelectionReport(
    Point StartClient,
    Point EndClient,
    Native.CursorPoint StartScreen,
    Native.CursorPoint EndScreen);

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var options = TestOptions.Parse(args);
        if (options.Help)
        {
            TestOptions.PrintHelp();
            return 0;
        }

        try
        {
            return options.AppPath is null
                ? RunFixture(options)
                : RunApplication(options);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL desktop test runner: {exception.Message}");
            return 1;
        }
    }

    private static int RunFixture(TestOptions options)
    {
        using var fixture = new SelectionFixtureForm();
        using var sentinel = new Form
        {
            Text = "LightTranslate Desktop Test Sentinel",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            Size = new Size(280, 100),
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow
        };

        fixture.Show();
        sentinel.Show();
        Native.SetForegroundWindow(sentinel.Handle);
        // Foreground activation is subject to Windows foreground-lock policy. Use the
        // sentinel when the OS permits it, but keep the test valid on a locked desktop.
        PumpUntil(() => Native.GetForegroundWindow() == sentinel.Handle, options.PhaseTimeout, "sentinel activation", throwOnTimeout: false);

        var originalForeground = Native.GetForegroundWindow();
        var text = fixture.SampleText;
        var edit = fixture.Editor.Handle;
        SendSelection(edit, 0, Math.Min(text.Length, 19));

        Require(
            PumpUntil(() => fixture.TranslationButton.Visible, options.PhaseTimeout, "fixture translation button"),
            "selection did not reveal the fixture translation button");
        Require(fixture.LastSelection == text[..19], "fixture did not receive the exact selected text");

        var foregroundAfterSelection = Native.GetForegroundWindow();
        Require(
            foregroundAfterSelection == originalForeground,
            $"selection changed the foreground window (before 0x{originalForeground.ToInt64():X}, after 0x{foregroundAfterSelection.ToInt64():X})");

        Native.SendMessage(fixture.TranslationButton.Handle, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        Require(
            PumpUntil(() => fixture.ResultLabel.Text.Contains("Demo translation", StringComparison.Ordinal), options.PhaseTimeout, "fixture translation result"),
            "clicking the translation button did not show a demo result");

        if (options.ScreenshotPath is not null)
        {
            fixture.SaveScreenshot(options.ScreenshotPath);
            Console.WriteLine($"PASS fixture screenshot: {Path.GetFullPath(options.ScreenshotPath)}");
        }

        fixture.Close();
        sentinel.Close();
        PumpUntil(() => !fixture.IsHandleCreated && !sentinel.IsHandleCreated, options.PhaseTimeout, "fixture shutdown");
        Console.WriteLine("PASS fixture selection, no-activation, demo translation, and shutdown");
        return 0;
    }

    private static int RunApplication(TestOptions options)
    {
        Native.GetCursorPos(out var originalCursor);
        using var fixture = new SelectionFixtureForm();
        fixture.TopMost = true;
        fixture.Show();
        Native.ShowWindow(fixture.Handle, Native.SW_SHOWNORMAL);
        Native.SetWindowPos(
            fixture.Handle,
            Native.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
        fixture.BringToFront();
        fixture.Activate();
        Application.DoEvents();
        Require(Native.IsWindowVisible(fixture.Handle), "the test fixture window is not visible after explicit ShowWindow");
        var lastTestCursor = originalCursor;

        var startInfo = new ProcessStartInfo
        {
            FileName = options.AppPath!,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.AppPath!)) ?? Environment.CurrentDirectory
        };

        foreach (var argument in options.AppArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start the application process");
        Console.WriteLine($"Started app pid {process.Id}");

        try
        {
            // GlobalMouseHook is installed asynchronously by the app. Give a cold .NET
            // process a bounded startup window, then use the harness-owned fixture as target.
            PumpDelay(TimeSpan.FromMilliseconds(options.StartupDelayMs));
            Require(!process.HasExited, "application exited before the desktop flow started");

            var edit = fixture.Editor.Handle;
            lastTestCursor = ActivateFixtureWithMouseClick(fixture);
            PumpUntil(() => Native.GetForegroundWindow() == fixture.Handle, options.PhaseTimeout, "fixture activation");
            DragSelectionReport? drag = null;
            var selectedText = string.Empty;
            try
            {
                drag = SelectFixtureWithMouseDrag(fixture);
                lastTestCursor = drag.Value.EndScreen;
                Require(
                    PumpUntil(() => !string.IsNullOrWhiteSpace(fixture.Editor.SelectedText), options.PhaseTimeout, "fixture physical selection"),
                    "physical drag did not produce a nonblank selection");
                selectedText = fixture.Editor.SelectedText.Trim();
                Require(selectedText.Length >= 4, $"physical drag selected too little text: '{fixture.Editor.SelectedText}'");
            }
            catch (Exception exception)
            {
                var failureScreenshot = SaveFixtureFailureScreenshot(fixture, options.ScreenshotPath);
                var dragSummary = drag is { } captured
                    ? $"startClient={captured.StartClient}, endClient={captured.EndClient}, " +
                      $"startScreen=({captured.StartScreen.X},{captured.StartScreen.Y}), " +
                      $"endScreen=({captured.EndScreen.X},{captured.EndScreen.Y})"
                    : "drag=not-started";
                var characterPositions = string.Join(", ", Enumerable.Range(0, Math.Min(fixture.SampleText.Length + 1, 28))
                    .Select(index =>
                    {
                        var position = fixture.Editor.GetPositionFromCharIndex(index);
                        return $"{index}=({position.X},{position.Y})";
                    }));
                var endCharIndex = drag is { } dragValue
                    ? fixture.Editor.GetCharIndexFromPosition(dragValue.EndClient)
                    : -1;
                throw new InvalidOperationException(
                    $"physical selection diagnostics: SelectedText='{fixture.Editor.SelectedText}', " +
                    $"SelectionStart={fixture.Editor.SelectionStart}, SelectionLength={fixture.Editor.SelectionLength}, " +
                    $"{dragSummary}, endCharIndex={endCharIndex}, positions=[{characterPositions}], error='{exception.Message}', " +
                    $"screenshot='{failureScreenshot}'",
                    exception);
            }

            var button = WaitForNearbyButton(process.Id, edit, options.ButtonTitle, options.PhaseTimeout);
            Require(button != IntPtr.Zero, "selection did not reveal a nearby translation button");
            Require(Native.GetForegroundWindow() == fixture.Handle, "selection activated the translation UI");

            // The fixture is topmost only to make activation deterministic. Once the app's
            // own topmost floating button is present, let it receive the real click normally.
            fixture.TopMost = false;
            Application.DoEvents();
            lastTestCursor = ClickWindow(button);
            var result = WaitForDemoResult(process.Id, options.ResultMarker, options.PhaseTimeout);
            Require(result != IntPtr.Zero, "translation button did not expose a demo result");
            Require(
                WindowTreeContainsText(result, selectedText),
                $"result window did not contain the exact text selected in the separate fixture: '{selectedText}'");

            if (options.ScreenshotPath is not null)
            {
                SaveWindowScreenshot(result, options.ScreenshotPath);
                Console.WriteLine($"PASS app screenshot: {Path.GetFullPath(options.ScreenshotPath)}");
            }

            Console.WriteLine("PASS app selection, no-activation, demo translation, and screenshot");
            return 0;
        }
        catch (Exception exception)
        {
            var failureScreenshot = SaveFixtureFailureScreenshot(fixture, options.ScreenshotPath);
            var appWindows = DescribeOwnedWindows(process.Id);
            throw new InvalidOperationException(
                $"app-flow diagnostics: {exception.Message}; fixture screenshot='{failureScreenshot}'; " +
                $"launched app windows=[{appWindows}]",
                exception);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    CloseOwnedWindows(process.Id);
                    process.CloseMainWindow();
                    if (!process.WaitForExit((int)options.PhaseTimeout.TotalMilliseconds))
                    {
                        // The process was started by this runner, so only this process is eligible
                        // for cleanup. Do not kill a process discovered by window enumeration.
                        process.Kill(entireProcessTree: false);
                        process.WaitForExit((int)options.PhaseTimeout.TotalMilliseconds);
                    }
                }

                Require(process.HasExited, "application process did not shut down");
            }
            finally
            {
                fixture.Close();
                PumpUntil(() => !fixture.IsHandleCreated, options.PhaseTimeout, "fixture shutdown", throwOnTimeout: false);
                if (Native.GetCursorPos(out var currentCursor) &&
                    currentCursor.X == lastTestCursor.X && currentCursor.Y == lastTestCursor.Y)
                {
                    Native.SetCursorPos(originalCursor.X, originalCursor.Y);
                }
            }
        }
    }

    private static void CloseOwnedWindows(int processId)
    {
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var ownerPid);
            if (ownerPid == processId && Native.IsWindowVisible(window))
            {
                Native.PostMessage(window, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);
    }

    private static string DescribeOwnedWindows(int processId)
    {
        var descriptions = new List<string>();
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out var ownerPid);
            if (ownerPid == processId)
            {
                var title = ReadWindowText(window).Replace('\r', ' ').Replace('\n', ' ');
                if (title.Length > 120)
                {
                    title = title[..120] + "…";
                }

                descriptions.Add(
                    $"0x{window.ToInt64():X}: visible={Native.IsWindowVisible(window)}, " +
                    $"class='{ReadClassName(window)}', title='{title}'");
            }

            return true;
        }, IntPtr.Zero);
        return descriptions.Count == 0 ? "none" : string.Join("; ", descriptions);
    }

    private static IntPtr WaitForNearbyButton(int ownerProcessId, IntPtr edit, string? buttonTitle, TimeSpan timeout)
    {
        Native.GetWindowRect(edit, out var editRect);
        IntPtr found = IntPtr.Zero;
        PumpUntil(() =>
        {
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out var ownerPid);
                if (ownerPid != ownerProcessId || !Native.IsWindowVisible(window))
                {
                    return true;
                }

                Native.GetWindowRect(window, out var windowRect);
                if (!RectsNear(editRect, windowRect))
                {
                    return true;
                }

                var title = ReadWindowText(window);
                if (!string.IsNullOrEmpty(buttonTitle) && title.Contains(buttonTitle, StringComparison.OrdinalIgnoreCase))
                {
                    found = window;
                    return false;
                }

                if (string.IsNullOrEmpty(buttonTitle) && ReadClassName(window).Contains("BUTTON", StringComparison.OrdinalIgnoreCase))
                {
                    found = window;
                    return false;
                }

                Native.EnumChildWindows(window, (child, _) =>
                {
                    if (!Native.IsWindowVisible(child) || !ReadClassName(child).Contains("BUTTON", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (string.IsNullOrEmpty(buttonTitle) || ReadWindowText(child).Contains(buttonTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        found = child;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero)
                {
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found != IntPtr.Zero;
        }, timeout, "nearby translation button");
        return found;
    }

    private static IntPtr WaitForDemoResult(int ownerProcessId, string resultMarker, TimeSpan timeout)
    {
        IntPtr found = IntPtr.Zero;
        PumpUntil(() =>
        {
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out var ownerPid);
                if (ownerPid != ownerProcessId || !Native.IsWindowVisible(window))
                {
                    return true;
                }

                var title = ReadWindowText(window);
                if (title.Contains(resultMarker, StringComparison.OrdinalIgnoreCase))
                {
                    found = window;
                    return false;
                }

                var resultWindow = title.Contains("Demo translation", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("翻译结果", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("轻译", StringComparison.OrdinalIgnoreCase);
                if (!resultWindow)
                {
                    return true;
                }

                Native.EnumChildWindows(window, (child, _) =>
                {
                    var text = ReadWindowText(child);
                    if (text.Contains(resultMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        found = window;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero)
                {
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found != IntPtr.Zero;
        }, timeout, "demo translation result", throwOnTimeout: false);
        return found;
    }

    private static bool WindowTreeContainsText(IntPtr root, string expected)
    {
        if (ReadWindowText(root).Contains(expected, StringComparison.Ordinal))
        {
            return true;
        }

        var found = false;
        Native.EnumChildWindows(root, (window, _) =>
        {
            if (ReadWindowText(window).Contains(expected, StringComparison.Ordinal))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static DragSelectionReport SelectFixtureWithMouseDrag(SelectionFixtureForm fixture)
    {
        var startClient = new Point(5, Math.Max(4, fixture.Editor.Font.Height / 2));
        var targetLength = Math.Min(19, fixture.SampleText.Length);
        var pointerIndex = Math.Max(0, targetLength - 2);
        var pointerOffset = fixture.Editor.GetPositionFromCharIndex(pointerIndex);
        var nextOffset = fixture.Editor.GetPositionFromCharIndex(Math.Min(pointerIndex + 1, fixture.SampleText.Length));
        var characterGap = Math.Max(1, nextOffset.X - pointerOffset.X);
        var endClient = new Point(
            Math.Max(startClient.X + 4, pointerOffset.X + Math.Max(1, characterGap / 2)),
            Math.Max(4, pointerOffset.Y + fixture.Editor.Font.Height / 2));
        var start = fixture.Editor.PointToScreen(startClient);
        var end = fixture.Editor.PointToScreen(endClient);

        if (!Native.SetCursorPos(start.X, start.Y))
        {
            throw new InvalidOperationException("could not move the cursor to the fixture selection");
        }
        if (Native.SendMouseInput(Native.MOUSEEVENTF_LEFTDOWN) != 1)
        {
            throw new InvalidOperationException("SendInput could not press the fixture selection");
        }

        PumpDelay(TimeSpan.FromMilliseconds(40));
        if (!Native.SetCursorPos(end.X, end.Y))
        {
            throw new InvalidOperationException("could not move the cursor across the fixture selection");
        }
        PumpDelay(TimeSpan.FromMilliseconds(40));
        if (Native.SendMouseInput(Native.MOUSEEVENTF_LEFTUP) != 1)
        {
            throw new InvalidOperationException("SendInput could not release the fixture selection");
        }

        PumpDelay(TimeSpan.FromMilliseconds(40));
        return new DragSelectionReport(
            startClient,
            endClient,
            new Native.CursorPoint { X = start.X, Y = start.Y },
            new Native.CursorPoint { X = end.X, Y = end.Y });
    }

    private static Native.CursorPoint ActivateFixtureWithMouseClick(SelectionFixtureForm fixture)
    {
        var clientPoint = new Point(8, Math.Max(4, fixture.Editor.Font.Height / 2));
        var attempts = new List<string>();
        foreach (var location in FixtureCandidateLocations(fixture))
        {
            if (!Native.SetWindowPos(
                    fixture.Handle,
                    Native.HWND_TOPMOST,
                    location.X,
                    location.Y,
                    0,
                    0,
                    Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW))
            {
                attempts.Add($"move ({location.X},{location.Y}) failed");
                continue;
            }

            fixture.BringToFront();
            Application.DoEvents();
            PumpDelay(TimeSpan.FromMilliseconds(60));

            var screenPoint = fixture.Editor.PointToScreen(clientPoint);
            var cursorPoint = new Native.CursorPoint { X = screenPoint.X, Y = screenPoint.Y };
            if (!IsOwnedByFixture(fixture, cursorPoint, out var ownership))
            {
                attempts.Add($"candidate ({location.X},{location.Y}) rejected: {ownership}");
                continue;
            }

            if (!Native.SetCursorPos(cursorPoint.X, cursorPoint.Y))
            {
                attempts.Add($"candidate ({location.X},{location.Y}) could not move the cursor");
                continue;
            }

            PumpDelay(TimeSpan.FromMilliseconds(40));
            if (!IsOwnedByFixture(fixture, cursorPoint, out ownership))
            {
                attempts.Add($"candidate ({location.X},{location.Y}) became covered: {ownership}");
                continue;
            }

            if (Native.SendMouseInput(Native.MOUSEEVENTF_LEFTDOWN) != 1)
            {
                throw new InvalidOperationException("SendInput could not press the fixture activation point");
            }

            PumpDelay(TimeSpan.FromMilliseconds(40));
            if (Native.SendMouseInput(Native.MOUSEEVENTF_LEFTUP) != 1)
            {
                throw new InvalidOperationException("SendInput could not release the fixture activation point");
            }

            PumpDelay(TimeSpan.FromMilliseconds(40));
            return cursorPoint;
        }

        throw new InvalidOperationException(
            "could not find an unoccluded fixture activation point without clicking another window: " +
            string.Join("; ", attempts));
    }

    private static IEnumerable<Point> FixtureCandidateLocations(SelectionFixtureForm fixture)
    {
        var seen = new HashSet<Point>();
        foreach (var screen in Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var left = area.Left + 16;
            var top = area.Top + 16;
            var right = Math.Max(left, area.Right - fixture.Width - 16);
            var bottom = Math.Max(top, area.Bottom - fixture.Height - 16);
            var center = new Point(
                area.Left + Math.Max(0, (area.Width - fixture.Width) / 2),
                area.Top + Math.Max(0, (area.Height - fixture.Height) / 2));

            foreach (var candidate in new[]
            {
                new Point(left, top),
                new Point(right, top),
                new Point(left, bottom),
                new Point(right, bottom),
                center
            })
            {
                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static bool IsOwnedByFixture(
        SelectionFixtureForm fixture,
        Native.CursorPoint point,
        out string diagnostic)
    {
        var windowAtPoint = Native.WindowFromPoint(point);
        var rootWindow = windowAtPoint == IntPtr.Zero
            ? IntPtr.Zero
            : Native.GetAncestor(windowAtPoint, Native.GA_ROOT);
        if (rootWindow == fixture.Handle)
        {
            diagnostic = string.Empty;
            return true;
        }

        Native.GetWindowRect(fixture.Handle, out var fixtureRect);
        diagnostic =
            $"visible={Native.IsWindowVisible(fixture.Handle)}, " +
            $"fixtureRect=({fixtureRect.Left},{fixtureRect.Top},{fixtureRect.Right},{fixtureRect.Bottom}), " +
            $"point=({point.X},{point.Y}), window 0x{windowAtPoint.ToInt64():X}, " +
            $"root 0x{rootWindow.ToInt64():X}, fixture 0x{fixture.Handle.ToInt64():X}";
        return false;
    }

    private static string SaveFixtureFailureScreenshot(SelectionFixtureForm fixture, string? requestedScreenshot)
    {
        var path = requestedScreenshot is null
            ? Path.Combine(Environment.CurrentDirectory, ".artifacts", "fixture-failure.png")
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(requestedScreenshot)) ?? Environment.CurrentDirectory,
                Path.GetFileNameWithoutExtension(requestedScreenshot) + ".fixture-failure.png");
        try
        {
            fixture.SaveScreenshot(path);
            return Path.GetFullPath(path);
        }
        catch (Exception screenshotException)
        {
            return $"unavailable ({screenshotException.Message})";
        }
    }

    private static Native.CursorPoint ClickWindow(IntPtr window)
    {
        if (!Native.GetWindowRect(window, out var rect))
        {
            throw new InvalidOperationException("could not read translation button bounds");
        }

        var center = new Native.CursorPoint
        {
            X = rect.Left + Math.Max(1, (rect.Right - rect.Left) / 2),
            Y = rect.Top + Math.Max(1, (rect.Bottom - rect.Top) / 2)
        };
        if (!Native.SetCursorPos(center.X, center.Y))
        {
            throw new InvalidOperationException("could not move the cursor to the translation button");
        }
        PumpDelay(TimeSpan.FromMilliseconds(40));
        if (Native.SendMouseInput(Native.MOUSEEVENTF_LEFTDOWN) != 1)
        {
            throw new InvalidOperationException("SendInput could not press the translation button");
        }

        PumpDelay(TimeSpan.FromMilliseconds(40));
        if (Native.SendMouseInput(Native.MOUSEEVENTF_LEFTUP) != 1)
        {
            throw new InvalidOperationException("SendInput could not release the translation button");
        }

        PumpDelay(TimeSpan.FromMilliseconds(40));
        return center;
    }

    private static void PumpDelay(TimeSpan duration)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static bool RectsNear(Native.Rect left, Native.Rect right)
    {
        var horizontalGap = Math.Max(0, Math.Max(left.Left - right.Right, right.Left - left.Right));
        var verticalGap = Math.Max(0, Math.Max(left.Top - right.Bottom, right.Top - left.Bottom));
        return horizontalGap <= 260 && verticalGap <= 180;
    }

    private static string ReadWindowText(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return string.Empty;
        }

        if (Native.SendMessageTimeout(
                window,
                Native.WM_GETTEXTLENGTH,
                IntPtr.Zero,
                IntPtr.Zero,
                Native.SMTO_ABORTIFHUNG | Native.SMTO_BLOCK,
                Native.WindowMessageTimeoutMs,
                out var lengthResult) == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = Math.Clamp(lengthResult.ToInt64(), 0, Native.MaxWindowTextLength);
        var builder = new StringBuilder((int)length + 1);
        if (Native.SendMessageTimeout(
                window,
                Native.WM_GETTEXT,
                new IntPtr(builder.Capacity),
                builder,
                Native.SMTO_ABORTIFHUNG | Native.SMTO_BLOCK,
                Native.WindowMessageTimeoutMs,
                out _) == IntPtr.Zero)
        {
            return string.Empty;
        }

        return builder.ToString();
    }

    private static string ReadClassName(IntPtr window)
    {
        var builder = new StringBuilder(256);
        Native.GetClassName(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private static void SendSelection(IntPtr edit, int start, int end)
    {
        Native.SendMessage(edit, Native.EM_SETSEL, new IntPtr(start), new IntPtr(end));
        Native.SendMessage(edit, Native.EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
    }

    private static void SaveWindowScreenshot(IntPtr window, string path)
    {
        Native.GetWindowRect(window, out var rect);
        var width = Math.Max(rect.Right - rect.Left, 1);
        var height = Math.Max(rect.Bottom - rect.Top, 1);
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!Native.PrintWindow(window, hdc, Native.PW_RENDERFULLCONTENT))
            {
                throw new InvalidOperationException("PrintWindow failed for the requested app window");
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout, string phase, bool throwOnTimeout = true)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Application.DoEvents();
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        if (throwOnTimeout)
        {
            throw new TimeoutException($"timed out during {phase} after {timeout.TotalMilliseconds:0} ms");
        }

        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class SelectionFixtureForm : Form
{
    public const string WindowTitle = "LightTranslate Desktop Test Fixture";
    public const string SampleTextValue = "The quick brown fox jumps over the lazy dog.";

    private readonly Label _hintLabel;

    public SelectionFixtureForm()
    {
        Text = WindowTitle;
        Name = "LightTranslate.DesktopTests.SelectionFixture";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(24, 24);
        ClientSize = new Size(760, 300);
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        Editor = new RichTextBox
        {
            Name = "SourceText",
            Text = SampleTextValue,
            Font = new Font("Segoe UI", 13f),
            Multiline = true,
            DetectUrls = false,
            HideSelection = false,
            Location = new Point(24, 56),
            Size = new Size(712, 130),
            TabIndex = 0
        };
        Editor.SelectionChanged += OnSelectionChanged;

        _hintLabel = new Label
        {
            AutoSize = true,
            Text = "Select text programmatically to reveal the nearby demo translation button.",
            Location = new Point(24, 20)
        };

        ResultLabel = new Label
        {
            Name = "TranslationResult",
            AutoSize = true,
            Location = new Point(24, 224),
            Text = "Demo result appears here after clicking Translate."
        };

        TranslationButton = new Button
        {
            Name = "TranslateButton",
            Text = "Translate",
            AutoSize = true,
            TabStop = false,
            Visible = false,
            UseVisualStyleBackColor = true
        };
        TranslationButton.Click += (_, _) =>
        {
            ResultLabel.Text = $"Demo translation: {LastSelection}";
        };

        Controls.Add(_hintLabel);
        Controls.Add(Editor);
        Controls.Add(TranslationButton);
        Controls.Add(ResultLabel);
    }

    public RichTextBox Editor { get; }

    public Button TranslationButton { get; }

    public Label ResultLabel { get; }

    public string SampleText => SampleTextValue;

    public string ExpectedSelection => SampleTextValue[..19];

    public string LastSelection { get; private set; } = string.Empty;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        LastSelection = Editor.SelectedText;
        if (string.IsNullOrWhiteSpace(LastSelection))
        {
            TranslationButton.Visible = false;
            return;
        }

        var anchor = Editor.GetPositionFromCharIndex(Editor.SelectionStart);
        anchor.Y += Math.Max(Editor.Font.Height, 18) + 3;
        var location = Editor.PointToClient(Editor.PointToScreen(anchor));
        location.X = Math.Clamp(location.X, 4, Math.Max(4, Editor.ClientSize.Width - TranslationButton.Width - 4));
        location.Y = Math.Clamp(location.Y, 4, Math.Max(4, Editor.ClientSize.Height - TranslationButton.Height - 4));
        TranslationButton.Location = new Point(Editor.Left + location.X, Editor.Top + location.Y);
        TranslationButton.Visible = true;
    }

    public void SaveScreenshot(string path)
    {
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bitmap, ClientRectangle);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory);
        bitmap.Save(path, ImageFormat.Png);
    }
}

internal sealed class TestOptions
{
    private TestOptions()
    {
    }

    public string? AppPath { get; private set; }

    public List<string> AppArguments { get; } = new();

    public string? ScreenshotPath { get; private set; }

    public string? ButtonTitle { get; private set; }

    public string ResultMarker { get; private set; } = "翻译完成";

    public TimeSpan PhaseTimeout { get; private set; } = TimeSpan.FromSeconds(5);

    public int StartupDelayMs { get; private set; } = 1500;

    public bool Help { get; private set; }

    public static TestOptions Parse(string[] args)
    {
        var result = new TestOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--help":
                case "-h":
                    result.Help = true;
                    break;
                case "--fixture":
                    break;
                case "--app":
                    result.AppPath = RequireValue(args, ref index, argument);
                    break;
                case "--app-arg":
                    result.AppArguments.Add(RequireValue(args, ref index, argument));
                    break;
                case "--screenshot":
                    result.ScreenshotPath = RequireValue(args, ref index, argument);
                    break;
                case "--button-title":
                    result.ButtonTitle = RequireValue(args, ref index, argument);
                    break;
                case "--result-marker":
                    result.ResultMarker = RequireValue(args, ref index, argument);
                    break;
                case "--timeout-ms":
                    var timeout = RequireValue(args, ref index, argument);
                    if (!int.TryParse(timeout, out var timeoutMs) || timeoutMs < 100)
                    {
                        throw new ArgumentException("--timeout-ms must be an integer >= 100");
                    }

                    result.PhaseTimeout = TimeSpan.FromMilliseconds(timeoutMs);
                    break;
                case "--startup-ms":
                    var startup = RequireValue(args, ref index, argument);
                    if (!int.TryParse(startup, out var startupMs) || startupMs < 0)
                    {
                        throw new ArgumentException("--startup-ms must be an integer >= 0");
                    }

                    result.StartupDelayMs = startupMs;
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{argument}' (use --help)");
            }
        }

        return result;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("CherryTranslate desktop integration tests");
        Console.WriteLine("  --fixture                         Run the offline WinForms fixture (default)");
        Console.WriteLine("  --app <exe> --app-arg <arg>       Launch app and drive the separate selection fixture");
        Console.WriteLine("  --button-title <text>             Translation button text filter (default: any nearby Button)");
        Console.WriteLine("  --result-marker <text>             Result/status marker (default: 翻译完成)");
        Console.WriteLine("  --screenshot <png>                 Save only the fixture or launched app window");
        Console.WriteLine("  --timeout-ms <ms>                  Timeout for each phase (default: 5000)");
        Console.WriteLine("  --startup-ms <ms>                  Wait for app cold startup (default: 1500)");
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value");
        }

        return args[index];
    }
}

internal static class Native
{
    internal const uint EM_SETSEL = 0x00B1;
    internal const uint EM_SCROLLCARET = 0x00B7;
    internal const uint BM_CLICK = 0x00F5;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_GETTEXT = 0x000D;
    internal const uint WM_GETTEXTLENGTH = 0x000E;
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const uint SMTO_BLOCK = 0x0001;
    internal const uint WindowMessageTimeoutMs = 250;
    internal const int MaxWindowTextLength = 16_384;
    internal const uint INPUT_MOUSE = 0;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint GA_ROOT = 2;
    internal const int SW_SHOWNORMAL = 1;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    internal delegate bool EnumChildProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(CursorPoint point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    internal static uint SendMouseInput(uint flags)
        => SendInput(
            1,
            new[]
            {
                new Input
                {
                    Type = INPUT_MOUSE,
                    Data = new InputUnion
                    {
                        Mouse = new MouseInput { Flags = flags }
                    }
                }
            },
            Marshal.SizeOf<Input>());

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        [In, Out] StringBuilder lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
}

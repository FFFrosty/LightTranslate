# DesktopTests

This is a bounded WinForms desktop harness for LightTranslate. The fixture itself does not call a translation service.

## Offline fixture

Run the isolated fixture on Windows with:

```powershell
dotnet run --project tests\DesktopTests\CherryTranslate.DesktopTests.csproj -- `
  --fixture --screenshot .artifacts\fixture.png
```

`--fixture` uses `EM_SETSEL` inside the test process. It checks the fixture's selection, nearby translation button, no-activation behavior, deterministic demo result, and shutdown. It does not launch the production executable and does not exercise the production global mouse hook or clipboard fallback.

## Production end-to-end demo

Build the application first, then run the real demo build from an interactive Windows desktop:

```powershell
dotnet run --project tests\DesktopTests\CherryTranslate.DesktopTests.csproj -- `
  --app .\artifacts\LightTranslate\LightTranslate.exe `
  --app-arg --demo `
  --app-arg --selftest `
  --app-arg --exit-after=15 `
  --button-title '译' `
  --timeout-ms 5000 `
  --startup-ms 1500 `
  --screenshot .artifacts\app.png
```

This flow launches the production executable with its real global mouse hook, creates a separate visible `RichTextBox` fixture, selects text with physical mouse input, clicks the real floating button, and checks the result window. It may move the mouse, activate the fixture window, and briefly use the clipboard when the production selection fallback is needed. Run it while the desktop is idle; the production clipboard capture service attempts to restore the original clipboard contents, and the harness only terminates the process it started.

The harness accepts `--button-title` and `--result-marker` when a demo build uses different visible text. CI builds this project but does not run the physical desktop flow.

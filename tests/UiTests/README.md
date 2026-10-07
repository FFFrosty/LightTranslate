# UI tests

These tests exercise the Cherry-style translation result window with a fake
translator. They run as a Windows Forms STA console process, use no network,
do not load the real profile, and do not drive the mouse or the user's
clipboard.

Button painting regressions compare complete pixel buffers after a black
prefill, hover exit, active-state changes, and disabling/re-enabling. They
exercise light and dark surface/card/muted backgrounds through a transparent
parent, and verify that native button painting does not replay neighbouring
parent content. Button click and accessibility behavior are also checked.

Run from the repository root:

```powershell
dotnet run --project tests/UiTests/CherryTranslate.UiTests.csproj -c Release
```

To exercise the logical 96-DPI layout in a separate test process, run:

```powershell
dotnet run --project tests/UiTests/CherryTranslate.UiTests.csproj -c Release -- --dpi-unaware
```

The switch changes only this test process's DPI awareness; it does not change
Windows or the display configuration, and it is not a substitute for testing
real multi-monitor DPI transitions. Its screenshots are written to
`.artifacts/ui-dpi-unaware/`, while the default run uses `.artifacts/ui/`.

The test writes visual-review screenshots to `.artifacts/ui/`. It records the
actual process DPI in `ui-result-dpi-<dpi>.png`, and also captures deterministic
window-resize samples at 150% and 200% of the base client size plus the narrow
layout. Resizing the window does not change the Windows DPI context. This
directory is ignored by Git and can be removed safely after review.

The test intentionally interacts through the form's public behavior and stable
control `Name`/`AccessibleName` values. If a UI behavior changes, update the
selector in `Program.cs` together with the corresponding production control's
accessibility name.

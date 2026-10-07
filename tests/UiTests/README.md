# UI tests

These tests exercise the Cherry-style translation result window with a fake
translator. They run as a Windows Forms STA console process, use no network,
do not load the real profile, and do not drive the mouse or the user's
clipboard.

Run from the repository root:

```powershell
dotnet run --project tests/UiTests/CherryTranslate.UiTests.csproj -c Release
```

The test writes visual-review screenshots to `.artifacts/ui/`. It records the
actual process DPI in `ui-result-dpi-<dpi>.png`, and also captures deterministic
window-resize samples at 150% and 200% of the base client size plus the narrow
layout. Resizing the window does not change the Windows DPI context. This
directory is ignored by Git and can be removed safely after review.

The test intentionally interacts through the form's public behavior and stable
control `Name`/`AccessibleName` values. If a UI behavior changes, update the
selector in `Program.cs` together with the corresponding production control's
accessibility name.

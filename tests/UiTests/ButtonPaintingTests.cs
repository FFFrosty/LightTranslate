using System.Reflection;

namespace CherryTranslate.UiTests;

internal static class ButtonPaintingTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run()
    {
        var assembly = Assembly.Load("LightTranslate");
        var iconType = assembly.GetType("CherryTranslate.App.UiIcon", throwOnError: true)!;
        using var host = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10_000, -10_000),
            ShowInTaskbar = false,
            ClientSize = new Size(240, 120)
        };
        using var surface = new Panel { Dock = DockStyle.Fill };
        using var transparentParent = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        host.Controls.Add(surface);
        surface.Controls.Add(transparentParent);
        var parentPaints = 0;
        transparentParent.Paint += (_, e) =>
        {
            parentPaints++;
            // Simulate adjacent language text that transparent native painting
            // used to copy into the disabled direction-arrow button.
            e.Graphics.Clear(Color.Magenta);
            e.Graphics.DrawString("English", SystemFonts.DefaultFont, Brushes.Red, 0, 0);
        };

        try
        {
            foreach (var typeName in new[] { "UiIconButton", "UiTextButton", "TranslateToolbarButton" })
            {
                var type = assembly.GetType($"CherryTranslate.App.{typeName}", throwOnError: true)!;
                object?[] arguments = typeName switch
                {
                    "UiIconButton" => new[] { Enum.Parse(iconType, "ArrowRight"), "PaintTest", "Direction" },
                    "UiTextButton" => new[] { (object)"PaintTest", "Translate", "翻译", Enum.Parse(iconType, "Translate") },
                    _ => Array.Empty<object?>()
                };
                using var button = (Button)Activator.CreateInstance(type, InstanceMembers, null, arguments, null)!;
                button.AutoSize = false;
                button.Dock = DockStyle.None;
                button.Size = typeName == "UiIconButton" ? new Size(32, 30) : new Size(130, 36);
                transparentParent.Controls.Add(button);
                host.Show();
                Application.DoEvents();
                Require(button.AccessibleRole == AccessibleRole.PushButton && !string.IsNullOrWhiteSpace(button.AccessibleName),
                    $"{typeName} lost its button accessibility metadata");
                var clicks = 0;
                button.Click += (_, _) => clicks++;
                button.PerformClick();
                Require(clicks == 1, $"{typeName} no longer supports PerformClick");

                using var reused = new Bitmap(button.Width, button.Height);
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    UiDiscovery.SetTheme(theme);
                    // Test both surface and card backgrounds, including an
                    // inherited background through a transparent container.
                    var palette = assembly.GetType("CherryTranslate.App.ThemeManager", true)!
                        .GetProperty("Palette", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                    foreach (var colorName in new[] { "Surface", "Card", "Muted" })
                    {
                        var background = (Color)palette.GetType().GetProperty(colorName)!.GetValue(palette)!;
                        surface.BackColor = background;
                        button.Enabled = true;
                        InvokeEvent(button, "OnMouseLeave");
                        using var clean = new Bitmap(button.Width, button.Height);
                        Fill(clean, background);
                        Paint(button, clean);

                        Fill(reused, Color.Black);
                        Paint(button, reused);
                        AssertSame(clean, reused, $"{typeName}/{theme}/{colorName}: incomplete repaint from black");
                        Require(reused.GetPixel(0, 0).ToArgb() == background.ToArgb(),
                            $"{typeName}/{theme}/{colorName}: rounded corner did not restore its parent's background");

                        InvokeEvent(button, "OnMouseEnter");
                        Paint(button, reused);
                        Require(!SamePixels(clean, reused), $"{typeName}: hover did not change the button");
                        InvokeEvent(button, "OnMouseLeave");
                        Paint(button, reused);
                        AssertSame(clean, reused, $"{typeName}: hover exit left dirty pixels");

                        if (typeName == "UiIconButton")
                        {
                            var active = type.GetProperty("IsActive")!;
                            active.SetValue(button, true);
                            Paint(button, reused);
                            Require(!SamePixels(clean, reused), "active icon did not retain its highlight");
                            active.SetValue(button, false);
                            Paint(button, reused);
                            AssertSame(clean, reused, "deactivating the icon left dirty pixels");
                        }

                        InvokeEvent(button, "OnMouseEnter");
                        Paint(button, reused);
                        button.Enabled = false;
                        button.PerformClick();
                        Require(clicks == 1, $"{typeName}: a disabled button accepted a click");
                        Paint(button, reused);
                        using var disabled = new Bitmap(button.Width, button.Height);
                        Fill(disabled, background);
                        Paint(button, disabled);
                        AssertSame(disabled, reused, $"{typeName}: disabling a hovered button left dirty pixels");
                        InvokeEvent(button, "OnMouseEnter");
                        Paint(button, reused);
                        AssertSame(disabled, reused, $"{typeName}: disabled button displayed a hover highlight");

                        // Also exercise WinForms' native print/paint path, rather
                        // than only calling the owner-paint method directly.
                        // Use a native baseline: TextRenderer's bitmap and window
                        // device contexts can rasterize glyphs differently.
                        using var nativeClean = new Bitmap(button.Width, button.Height);
                        Fill(nativeClean, background);
                        button.DrawToBitmap(nativeClean, button.ClientRectangle);
                        parentPaints = 0;
                        Fill(reused, Color.Black);
                        button.DrawToBitmap(reused, button.ClientRectangle);
                        Require(parentPaints == 0, $"{typeName}: native paint replayed parent content");
                        AssertSame(nativeClean, reused, $"{typeName}: native disabled paint left dirty pixels");
                        Require(reused.GetPixel(0, 0).ToArgb() == background.ToArgb(),
                            $"{typeName}: native paint left a dirty corner");
                        button.Enabled = true;
                        Paint(button, reused);
                        AssertSame(clean, reused, $"{typeName}: re-enabling retained an obsolete hover state");
                    }
                }
                transparentParent.Controls.Remove(button);
            }
        }
        finally
        {
            host.Close();
            UiDiscovery.SetTheme("Follow");
        }
    }

    private static void Paint(Control control, Bitmap target)
    {
        using var graphics = Graphics.FromImage(target);
        using var args = new PaintEventArgs(graphics, control.ClientRectangle);
        control.GetType().GetMethod("OnPaint", InstanceMembers)!.Invoke(control, new object[] { args });
    }

    private static void InvokeEvent(Control control, string name)
        => control.GetType().GetMethod(name, InstanceMembers)!.Invoke(control, new object[] { EventArgs.Empty });

    private static void Fill(Bitmap bitmap, Color color)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
    }

    private static void AssertSame(Bitmap expected, Bitmap actual, string message)
        => Require(SamePixels(expected, actual), message);

    private static bool SamePixels(Bitmap expected, Bitmap actual)
    {
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                if (expected.GetPixel(x, y).ToArgb() != actual.GetPixel(x, y).ToArgb())
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

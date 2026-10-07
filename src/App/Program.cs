using System.Threading;

namespace CherryTranslate.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var selfTest = HasFlag(args, "--selftest") || HasFlag(args, "--demo");
        var smoke = HasFlag(args, "--smoke");
        var diagnostics = HasFlag(args, "--diagnostics");

        using var instance = new Mutex(true, "Local\\LightTranslate.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("轻译已经在运行。", "轻译", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        if (selfTest)
        {
            DemoTrace.Configure(ReadTracePath(args));
            DemoTrace.Write("process", "demo_start");
            using var demoContext = new TrayApplicationContext(demoMode: true, ReadExitAfter(args));
            Application.Run(demoContext);
            DemoTrace.Write("process", "demo_exit");
            DemoTrace.Close();
            return 0;
        }

        if (smoke)
        {
            var result = new ProfileRuntime().LoadOrImport();
            return result.Profile is null ? 1 : 0;
        }

        if (diagnostics)
        {
            var result = new ProfileRuntime().LoadOrImport();
            using var form = new DiagnosticsForm(result);
            Application.Run(form);
            return 0;
        }

        using var context = new TrayApplicationContext();
        Application.Run(context);
        return 0;
    }

    private static bool HasFlag(IEnumerable<string> args, string flag)
        => args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static int? ReadExitAfter(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--exit-after", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Count &&
                int.TryParse(args[index + 1], out var seconds))
            {
                return Math.Clamp(seconds, 1, 60);
            }

            const string prefix = "--exit-after=";
            if (args[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index][prefix.Length..], out seconds))
            {
                return Math.Clamp(seconds, 1, 60);
            }
        }

        return null;
    }

    private static string? ReadTracePath(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--trace", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Count)
            {
                return args[index + 1];
            }

            const string prefix = "--trace=";
            if (args[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[index][prefix.Length..];
            }
        }

        return Environment.GetEnvironmentVariable("LIGHTTRANSLATE_DEMO_TRACE");
    }
}

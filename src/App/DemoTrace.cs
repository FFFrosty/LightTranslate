using System.Text;
using System.IO;

namespace CherryTranslate.App;

/// <summary>
/// Opt-in diagnostics for the offline desktop harness. The trace is deliberately
/// limited to control flow and lengths; it never records selected text or profile data.
/// </summary>
internal static class DemoTrace
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static bool Enabled { get; private set; }

    public static void Configure(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(fullPath, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };
            Enabled = true;
            Write("trace", "enabled");
        }
        catch
        {
            Enabled = false;
            _writer = null;
        }
    }

    public static void Write(string eventName, string details = "")
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                _writer?.WriteLine($"{DateTime.UtcNow:O}\t{eventName}\t{details}");
            }
        }
        catch
        {
            // Diagnostics must never change selection behavior.
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // Best effort during shutdown.
            }
            finally
            {
                _writer = null;
                Enabled = false;
            }
        }
    }
}

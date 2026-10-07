using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CherryTranslate.App;

internal sealed record ClipboardCaptureResult(bool Success, string? Text, string? Error)
{
    public static ClipboardCaptureResult Failed(string error) => new(false, null, error);
}

internal sealed class ClipboardCaptureService
{
    private static readonly SemaphoreSlim CopyGate = new(1, 1);
    private readonly IntPtr _ownWindow;

    public ClipboardCaptureService(IntPtr ownWindow)
    {
        _ownWindow = ownWindow;
    }

    public async Task<ClipboardCaptureResult> CopySelectionAsync(
        IntPtr targetWindow,
        CancellationToken cancellationToken)
    {
        if (!await CopyGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
        {
            return ClipboardCaptureResult.Failed("正在读取另一个选区，请稍后重试。");
        }

        try
        {
            return await CopySelectionCoreAsync(targetWindow, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            CopyGate.Release();
        }
    }

    private async Task<ClipboardCaptureResult> CopySelectionCoreAsync(
        IntPtr targetWindow,
        CancellationToken cancellationToken)
    {
        if (targetWindow == IntPtr.Zero || IsOurProcessWindow(targetWindow))
        {
            return ClipboardCaptureResult.Failed("没有找到可读取的原窗口。");
        }

        if (NativeMethods.GetForegroundWindow() != targetWindow)
        {
            NativeMethods.SetForegroundWindow(targetWindow);
            await Task.Delay(70, cancellationToken).ConfigureAwait(true);
        }

        if (NativeMethods.GetForegroundWindow() != targetWindow)
        {
            return ClipboardCaptureResult.Failed("原窗口已切换，未读取文本。");
        }

        ClipboardSnapshot original;
        try
        {
            original = ClipboardSnapshot.Capture();
            if (NativeMethods.GetClipboardSequenceNumber() != original.Sequence)
            {
                original.Dispose();
                return ClipboardCaptureResult.Failed("剪贴板正在变化，因此没有执行复制。");
            }
        }
        catch
        {
            return ClipboardCaptureResult.Failed("无法安全保存当前剪贴板内容，因此没有执行复制。");
        }

        var beforeCopySequence = original.Sequence;
        var text = string.Empty;
        var copiedSequence = beforeCopySequence;

        try
        {
            if (NativeMethods.GetForegroundWindow() != targetWindow ||
                NativeMethods.GetClipboardSequenceNumber() != beforeCopySequence)
            {
                return ClipboardCaptureResult.Failed("原窗口或剪贴板已变化，未读取文本。");
            }

            SendControlC();
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(900);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                copiedSequence = NativeMethods.GetClipboardSequenceNumber();
                if (copiedSequence != beforeCopySequence)
                {
                    break;
                }
                await Task.Delay(45, cancellationToken).ConfigureAwait(true);
            }

            if (copiedSequence == beforeCopySequence ||
                !IsClipboardOwnedByProcess(targetWindow) ||
                !Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                return ClipboardCaptureResult.Failed("当前应用没有提供可复制的文本选区。");
            }

            text = Clipboard.GetText(TextDataFormat.UnicodeText).Trim();
            if (NativeMethods.GetClipboardSequenceNumber() != copiedSequence ||
                !IsClipboardOwnedByProcess(targetWindow))
            {
                return ClipboardCaptureResult.Failed("剪贴板已被其他操作修改，未读取文本。");
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return ClipboardCaptureResult.Failed("选区为空。");
            }

            if (text.Length > 12_000)
            {
                return ClipboardCaptureResult.Failed("选区过长，请缩小后重试。");
            }

            return new ClipboardCaptureResult(true, text, null);
        }
        catch (OperationCanceledException)
        {
            return ClipboardCaptureResult.Failed("已取消读取选区。");
        }
        catch
        {
            return ClipboardCaptureResult.Failed("读取剪贴板失败，原内容将尽量恢复。");
        }
        finally
        {
            // Only restore when the clipboard still belongs to this explicit copy. If the
            // user copied something else while we were waiting, leave their new contents alone.
            try
            {
                var currentSequence = NativeMethods.GetClipboardSequenceNumber();
                if (copiedSequence != beforeCopySequence &&
                    currentSequence == copiedSequence &&
                    IsClipboardOwnedByProcess(targetWindow))
                {
                    original.Restore();
                }
            }
            catch
            {
                // Restoration is best effort; never replace a newer user clipboard change.
            }
            finally
            {
                original.Dispose();
            }
        }
    }

    private static void SendControlC()
    {
        var inputs = new[]
        {
            KeyboardInput(NativeMethods.VK_CONTROL, 0),
            KeyboardInput(NativeMethods.VK_C, 0),
            KeyboardInput(NativeMethods.VK_C, NativeMethods.KEYEVENTF_KEYUP),
            KeyboardInput(NativeMethods.VK_CONTROL, NativeMethods.KEYEVENTF_KEYUP)
        };

        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) != inputs.Length)
        {
            throw new InvalidOperationException("无法向原窗口发送复制操作。");
        }
    }

    private static NativeMethods.Input KeyboardInput(ushort key, uint flags)
        => new()
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = key,
                    Flags = flags
                }
            }
        };

    private bool IsOurProcessWindow(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var targetProcess);
            NativeMethods.GetWindowThreadProcessId(_ownWindow, out var ownProcess);
            return targetProcess != 0 && targetProcess == ownProcess;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsClipboardOwnedByProcess(IntPtr targetWindow)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(targetWindow, out var targetProcess);
            var owner = NativeMethods.GetClipboardOwner();
            if (targetProcess == 0 || owner == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(owner, out var ownerProcess);
            return ownerProcess == targetProcess;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class ClipboardSnapshot : IDisposable
{
    private ClipboardSnapshot(DataObject data, uint sequence)
    {
        Data = data;
        Sequence = sequence;
    }

    private DataObject Data { get; }
    public uint Sequence { get; }

    public static ClipboardSnapshot Capture()
    {
        var sequence = NativeMethods.GetClipboardSequenceNumber();
        var source = Clipboard.GetDataObject();
        var copy = new DataObject();
        if (source is not null)
        {
            foreach (var format in source.GetFormats(false))
            {
                var value = source.GetData(format, false)
                    ?? throw new InvalidOperationException("Clipboard format cannot be materialized");
                copy.SetData(format, CloneValue(value));
            }
        }

        if (NativeMethods.GetClipboardSequenceNumber() != sequence)
        {
            throw new InvalidOperationException("Clipboard changed while being captured");
        }

        return new ClipboardSnapshot(copy, sequence);
    }

    public void Restore()
    {
        if (Data.GetFormats(false).Length == 0)
        {
            Clipboard.Clear();
            return;
        }

        Clipboard.SetDataObject(Data, true);
    }

    public void Dispose()
    {
        foreach (var format in Data.GetFormats(false))
        {
            try
            {
                if (Data.GetData(format, false) is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // One clipboard format must not prevent other formats from being released.
            }
        }
    }

    private static object CloneValue(object value)
    {
        switch (value)
        {
            case string text:
                return text;
            case string[] strings:
                return strings.ToArray();
            case byte[] bytes:
                return bytes.ToArray();
            case char[] chars:
                return chars.ToArray();
            case StringCollection collection:
                var stringsCopy = new StringCollection();
                stringsCopy.AddRange(collection.Cast<string>().ToArray());
                return stringsCopy;
            case Bitmap bitmap:
                return new Bitmap(bitmap);
            case Image image:
                return new Bitmap(image);
            case Stream stream:
                if (!stream.CanRead)
                {
                    throw new InvalidOperationException("Clipboard stream cannot be read");
                }
                var position = stream.CanSeek ? stream.Position : 0;
                try
                {
                    if (stream.CanSeek)
                    {
                        stream.Position = 0;
                    }
                    var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    memory.Position = 0;
                    if (stream.CanSeek)
                    {
                        stream.Position = position;
                    }
                    return memory;
                }
                catch
                {
                    throw new InvalidOperationException("Clipboard stream cannot be materialized");
                }
            case Uri uri:
                return new Uri(uri.OriginalString, uri.IsAbsoluteUri ? UriKind.Absolute : UriKind.Relative);
            case ICloneable cloneable:
                try
                {
                    return cloneable.Clone() ?? throw new InvalidOperationException("Clipboard data clone returned null");
                }
                catch
                {
                    throw new InvalidOperationException("Clipboard format cannot be safely copied");
                }
            case int or uint or short or ushort or long or ulong or byte or sbyte or bool or float or double or decimal:
                return value;
            default:
                throw new InvalidOperationException("Clipboard format cannot be safely copied");
        }
    }
}

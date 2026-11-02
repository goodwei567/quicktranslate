using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace QuickTranslate.Services;

public sealed class SelectionCaptureService
{
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private static readonly ushort[] CopyBlockingKeys = [0x10, VkControl, 0x12, 0x5B, 0x5C, VkC];
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    public Task<string?> CaptureSelectedTextAsync(CancellationToken cancellationToken = default) =>
        CaptureSelectedTextAsync(GetForegroundWindow(), cancellationToken);

    public async Task<string?> CaptureSelectedTextAsync(IntPtr sourceWindow, CancellationToken cancellationToken = default,
        uint triggerKey = 0x54)
    {
        if (!await _captureGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("正在读取选中文字，请稍后重试。");
        }

        try
        {
            EnsureSourceWindow(sourceWindow);
            if (!ElevationService.CanSendInputTo(sourceWindow))
            {
                var processName = ElevationService.GetWindowProcessName(sourceWindow) ?? "目标程序";
                throw new ProtectedWindowCaptureException(
                    $"“{processName}”以管理员权限运行，而 QuickTranslate 当前是普通权限。" +
                    "Windows 会阻止普通程序向管理员窗口发送 Ctrl+C。" +
                    "请切换到管理员模式，或让该程序以普通权限重新打开。");
            }

            await WaitForShortcutReleaseAsync(sourceWindow, triggerKey, cancellationToken);
            var snapshot = await TrySnapshotClipboardAsync(cancellationToken);
            await WaitForShortcutReleaseAsync(sourceWindow, triggerKey, cancellationToken);

            var sequenceBeforeCopy = GetClipboardSequenceNumber();
            if (snapshot?.Sequence != sequenceBeforeCopy)
            {
                snapshot = null;
            }

            SendCopyShortcut();
            uint? capturedSequence = null;
            using var copyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            copyTimeout.CancelAfter(TimeSpan.FromSeconds(2));

            try
            {
                while (true)
                {
                    copyTimeout.Token.ThrowIfCancellationRequested();
                    EnsureSourceWindow(sourceWindow);
                    var sequence = GetClipboardSequenceNumber();

                    // Never treat pre-existing clipboard data as the result of this copy.
                    if (sequence != sequenceBeforeCopy)
                    {
                        try
                        {
                            var text = await ReadClipboardTextAsync(copyTimeout.Token);
                            EnsureSourceWindow(sourceWindow);
                            if (sequence == GetClipboardSequenceNumber() && !string.IsNullOrWhiteSpace(text))
                            {
                                capturedSequence = sequence;
                                return text.Trim();
                            }
                        }
                        catch (COMException)
                        {
                            // Another process may briefly hold the clipboard while publishing data.
                        }
                    }

                    await Task.Delay(25, copyTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                if (snapshot is not null && capturedSequence is uint sequence &&
                    GetClipboardSequenceNumber() == sequence)
                {
                    await TryRestoreClipboardAsync(snapshot, sequence);
                }
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private static async Task WaitForShortcutReleaseAsync(IntPtr sourceWindow, uint triggerKey, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceWindow(sourceWindow);
            if (!CopyBlockingKeys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0) &&
                (GetAsyncKeyState((int)triggerKey) & 0x8000) == 0)
            {
                return;
            }

            if (elapsed.Elapsed >= TimeSpan.FromSeconds(3))
            {
                throw new InvalidOperationException("请先松开 Ctrl、Shift 等快捷键，再重新尝试。");
            }

            await Task.Delay(15, cancellationToken);
        }
    }

    private static void EnsureSourceWindow(IntPtr sourceWindow)
    {
        if (sourceWindow == IntPtr.Zero || GetForegroundWindow() != sourceWindow)
        {
            throw new InvalidOperationException("读取选区时前台窗口发生变化，请保持选中文字的窗口在前台后重试。");
        }
    }

    private static async Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
        {
            return null;
        }

        return await content.GetTextAsync().AsTask(cancellationToken);
    }

    internal static async Task<ClipboardSnapshot?> TrySnapshotClipboardAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));

        try
        {
            var sequence = GetClipboardSequenceNumber();
            var content = Clipboard.GetContent();
            var formats = content.AvailableFormats.ToArray();
            if (formats.Length == 0 && CountClipboardFormats() > 0)
            {
                return null;
            }

            var package = new DataPackage();
            foreach (var format in formats)
            {
                if (format == StandardDataFormats.Text)
                {
                    package.SetText(await content.GetTextAsync().AsTask(timeout.Token));
                }
                else if (format == StandardDataFormats.Html)
                {
                    package.SetHtmlFormat(await content.GetHtmlFormatAsync().AsTask(timeout.Token));
                }
                else if (format == StandardDataFormats.Rtf)
                {
                    package.SetRtf(await content.GetRtfAsync().AsTask(timeout.Token));
                }
                else if (format == StandardDataFormats.Bitmap)
                {
                    var bitmap = await content.GetBitmapAsync().AsTask(timeout.Token);
                    using var stream = await bitmap.OpenReadAsync().AsTask(timeout.Token);
                    var copy = await CloneStreamAsync(stream, timeout.Token);
                    package.SetBitmap(RandomAccessStreamReference.CreateFromStream(copy));
                }
                else if (format == StandardDataFormats.StorageItems)
                {
                    package.SetStorageItems(await content.GetStorageItemsAsync().AsTask(timeout.Token));
                }
                else if (format == StandardDataFormats.WebLink)
                {
                    package.SetWebLink(await content.GetWebLinkAsync().AsTask(timeout.Token));
                }
                else if (format == StandardDataFormats.ApplicationLink)
                {
                    package.SetApplicationLink(await content.GetApplicationLinkAsync().AsTask(timeout.Token));
                }
                else
                {
                    var data = await content.GetDataAsync(format).AsTask(timeout.Token);
                    if (data is IRandomAccessStream stream)
                    {
                        // Materialize custom data before the original clipboard owner is replaced.
                        package.SetData(format, await CloneStreamAsync(stream, timeout.Token));
                    }
                    else if (data is string)
                    {
                        package.SetData(format, data);
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            return sequence == GetClipboardSequenceNumber()
                ? new ClipboardSnapshot(sequence, package, formats.Length == 0)
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (COMException)
        {
            // Do not restore a partial snapshot and discard rich text, images, or files.
            return null;
        }
    }

    private static async Task<InMemoryRandomAccessStream> CloneStreamAsync(IRandomAccessStream source, CancellationToken cancellationToken)
    {
        var copy = new InMemoryRandomAccessStream();
        try
        {
            using var input = source.GetInputStreamAt(0);
            await RandomAccessStream.CopyAsync(input, copy).AsTask(cancellationToken);
            copy.Seek(0);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    private static async Task TryRestoreClipboardAsync(ClipboardSnapshot snapshot, uint capturedSequence)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (GetClipboardSequenceNumber() != capturedSequence)
            {
                return;
            }

            try
            {
                if (snapshot.IsEmpty)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetContent(snapshot.Package);
                }
                return;
            }
            catch (COMException)
            {
                // Retry brief clipboard locks, but never overwrite a newer copy from the user.
                await Task.Delay(20);
            }
        }
    }

    private static void SendCopyShortcut()
    {
        INPUT[] inputs = [Key(VkControl), Key(VkC), Key(VkC, keyUp: true), Key(VkControl, keyUp: true)];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            if (sent > 0)
            {
                INPUT[] releases = [Key(VkC, keyUp: true), Key(VkControl, keyUp: true)];
                _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
            }

            throw new InvalidOperationException(
                "无法发送复制快捷键。请确认目标程序允许复制，并与 QuickTranslate 使用相同权限运行。" +
                (error == 0 ? string.Empty : $"（Win32 错误：{error}）"));
        }
    }

    private static INPUT Key(ushort virtualKey, bool keyUp = false) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    internal sealed record ClipboardSnapshot(uint Sequence, DataPackage Package, bool IsEmpty);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern int CountClipboardFormats();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInput);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;

        // Include the largest union member so INPUT has its native size (40 bytes on x64).
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public enum HotkeyAction
{
    Selection,
    QuickAsk
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int SelectionHotkeyId = 0x514;
    private const int SelectionCandidateId = 0x515;
    private const int AskHotkeyId = 0x516;
    private const int AskCandidateId = 0x517;
    private const uint ModNoRepeat = 0x4000;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint WmChangeHotkey = 0x8051;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<ChangeRequest> _changes = new();
    private Thread? _thread;
    private uint _threadId;
    private int _selectionRegisteredId;
    private int _askRegisteredId;
    private volatile bool _disposed;
    private HotkeyGesture _selectionHotkey = HotkeyGesture.Default;
    private HotkeyGesture _askHotkey = HotkeyGesture.DefaultAsk;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public bool IsRegistered => _selectionRegisteredId != 0;
    public bool IsAskRegistered => _askRegisteredId != 0;
    public HotkeyGesture CurrentHotkey => _selectionHotkey;
    public HotkeyGesture CurrentAskHotkey => _askHotkey;
    public int LastErrorCode { get; private set; }
    public int AskLastErrorCode { get; private set; }

    public void Start(HotkeyGesture? selectionHotkey = null, HotkeyGesture? askHotkey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        _selectionHotkey = selectionHotkey ?? HotkeyGesture.Default;
        _askHotkey = askHotkey ?? HotkeyGesture.DefaultAsk;
        if (_selectionHotkey.ValidationError is { } selectionError)
        {
            throw new ArgumentException(selectionError, nameof(selectionHotkey));
        }
        if (_askHotkey.ValidationError is { } askError)
        {
            throw new ArgumentException(askError, nameof(askHotkey));
        }

        _thread = new Thread(HotkeyLoop)
        {
            IsBackground = true,
            Name = "QuickTranslate.GlobalHotkey"
        };
        _thread.Start();
        if (!_ready.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            throw new InvalidOperationException("全局快捷键服务未能及时启动。");
        }
    }

    public Task ChangeAsync(HotkeyGesture hotkey) => ChangeAsync(HotkeyAction.Selection, hotkey);

    public Task ChangeAskAsync(HotkeyGesture hotkey) => ChangeAsync(HotkeyAction.QuickAsk, hotkey);

    private Task ChangeAsync(HotkeyAction action, HotkeyGesture hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hotkey.ValidationError is { } error)
        {
            throw new ArgumentException(error, nameof(hotkey));
        }

        Start();
        if (_thread is not { IsAlive: true })
        {
            throw new InvalidOperationException("全局快捷键服务已停止，请重启应用。");
        }
        var request = new ChangeRequest(action, hotkey);
        _changes.Enqueue(request);
        if (!PostThreadMessage(_threadId, WmChangeHotkey, IntPtr.Zero, IntPtr.Zero))
        {
            request.Completion.TrySetException(new InvalidOperationException("无法更新快捷键，请重启 QuickTranslate 后重试。"));
        }
        return request.Completion.Task;
    }

    private void HotkeyLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            // Create the message queue before accepting update or shutdown messages.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            TryRegisterHotkey(HotkeyAction.Selection, _selectionHotkey, out _);
            // A conflict on the optional free-form shortcut must not disable selection capture.
            TryRegisterHotkey(HotkeyAction.QuickAsk, _askHotkey, out _);
            _ready.TrySetResult();

            // Keep running after a conflict so a different shortcut can be chosen in settings.
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WmHotkey)
                {
                    var id = message.wParam.ToInt32();
                    var action = id == _selectionRegisteredId
                        ? HotkeyAction.Selection
                        : id == _askRegisteredId
                            ? HotkeyAction.QuickAsk
                            : (HotkeyAction?)null;
                    if (action is { } kind)
                    {
                        var triggerKey = (uint)((message.lParam.ToInt64() >> 16) & 0xFFFF);
                        HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(GetForegroundWindow(), triggerKey, kind));
                    }
                }
                else if (message.message == WmChangeHotkey)
                {
                    while (_changes.TryDequeue(out var request))
                    {
                        if (request.Completion.Task.IsCompleted)
                        {
                            continue;
                        }

                        if (TryRegisterHotkey(request.Action, request.Hotkey, out var errorCode))
                        {
                            request.Completion.TrySetResult();
                        }
                        else
                        {
                            var label = request.Action == HotkeyAction.Selection ? "划词快捷键" : "自由提问快捷键";
                            request.Completion.TrySetException(new InvalidOperationException(
                                $"无法注册{label} {request.Hotkey.DisplayText}，该组合可能被其他程序占用或由系统保留（Win32 错误：{errorCode}）。原快捷键保持不变。"));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
        finally
        {
            if (_selectionRegisteredId != 0)
            {
                UnregisterHotKey(IntPtr.Zero, _selectionRegisteredId);
            }
            if (_askRegisteredId != 0)
            {
                UnregisterHotKey(IntPtr.Zero, _askRegisteredId);
            }
            _selectionRegisteredId = 0;
            _askRegisteredId = 0;
            while (_changes.TryDequeue(out var request))
            {
                request.Completion.TrySetException(new InvalidOperationException("全局快捷键服务已停止，请重启应用。"));
            }
        }
    }

    private bool TryRegisterHotkey(HotkeyAction action, HotkeyGesture hotkey, out int errorCode)
    {
        var isSelection = action == HotkeyAction.Selection;
        var currentId = isSelection ? _selectionRegisteredId : _askRegisteredId;
        var current = isSelection ? _selectionHotkey : _askHotkey;
        if (currentId != 0 && hotkey == current)
        {
            errorCode = 0;
            return true;
        }

        // Register the replacement first; a conflict must not disable the working shortcut.
        var primaryId = isSelection ? SelectionHotkeyId : AskHotkeyId;
        var candidateId = currentId == primaryId
            ? (isSelection ? SelectionCandidateId : AskCandidateId)
            : primaryId;
        if (!RegisterHotKey(IntPtr.Zero, candidateId, hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey))
        {
            errorCode = Marshal.GetLastWin32Error();
            if (isSelection)
            {
                LastErrorCode = errorCode;
            }
            else
            {
                AskLastErrorCode = errorCode;
            }
            return false;
        }

        if (currentId != 0)
        {
            UnregisterHotKey(IntPtr.Zero, currentId);
        }
        if (isSelection)
        {
            _selectionRegisteredId = candidateId;
            _selectionHotkey = hotkey;
            LastErrorCode = 0;
        }
        else
        {
            _askRegisteredId = candidateId;
            _askHotkey = hotkey;
            AskLastErrorCode = 0;
        }
        errorCode = 0;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    private sealed record ChangeRequest(HotkeyAction Action, HotkeyGesture Hotkey)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMessage);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pointX;
        public int pointY;
    }
}

public sealed class HotkeyPressedEventArgs(IntPtr foregroundWindow, uint triggerKey, HotkeyAction action) : EventArgs
{
    public IntPtr ForegroundWindow { get; } = foregroundWindow;
    public uint TriggerKey { get; } = triggerKey;
    public HotkeyAction Action { get; } = action;
}

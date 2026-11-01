using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int FirstHotkeyId = 0x514;
    private const int SecondHotkeyId = 0x515;
    private const uint ModNoRepeat = 0x4000;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint WmChangeHotkey = 0x8051;

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<ChangeRequest> _changes = new();
    private Thread? _thread;
    private uint _threadId;
    private int _registeredHotkeyId;
    private volatile bool _registered;
    private volatile bool _disposed;
    private HotkeyGesture _currentHotkey = HotkeyGesture.Default;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public bool IsRegistered => _registered;
    public HotkeyGesture CurrentHotkey => _currentHotkey;
    public int LastErrorCode { get; private set; }

    public void Start(HotkeyGesture? hotkey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        _currentHotkey = hotkey ?? HotkeyGesture.Default;
        if (_currentHotkey.ValidationError is { } error)
        {
            throw new ArgumentException(error, nameof(hotkey));
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

    public Task ChangeAsync(HotkeyGesture hotkey)
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
        var request = new ChangeRequest(hotkey);
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
            TryRegisterHotkey(_currentHotkey);
            _ready.TrySetResult();

            // Keep running after a conflict so a different shortcut can be chosen in settings.
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WmHotkey && _registered && message.wParam.ToInt32() == _registeredHotkeyId)
                {
                    var triggerKey = (uint)((message.lParam.ToInt64() >> 16) & 0xFFFF);
                    HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(GetForegroundWindow(), triggerKey));
                }
                else if (message.message == WmChangeHotkey)
                {
                    while (_changes.TryDequeue(out var request))
                    {
                        if (request.Completion.Task.IsCompleted)
                        {
                            continue;
                        }

                        if (TryRegisterHotkey(request.Hotkey))
                        {
                            request.Completion.TrySetResult();
                        }
                        else
                        {
                            request.Completion.TrySetException(new InvalidOperationException(
                                $"无法注册 {request.Hotkey.DisplayText}，该组合可能被其他程序占用或由系统保留（Win32 错误：{LastErrorCode}）。原快捷键保持不变。"));
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
            if (_registered)
            {
                UnregisterHotKey(IntPtr.Zero, _registeredHotkeyId);
            }
            _registered = false;
            while (_changes.TryDequeue(out var request))
            {
                request.Completion.TrySetException(new InvalidOperationException("全局快捷键服务已停止，请重启应用。"));
            }
        }
    }

    private bool TryRegisterHotkey(HotkeyGesture hotkey)
    {
        if (_registered && hotkey == _currentHotkey)
        {
            LastErrorCode = 0;
            return true;
        }

        // Register the replacement first; a conflict must not disable the working shortcut.
        var candidateId = _registeredHotkeyId == FirstHotkeyId ? SecondHotkeyId : FirstHotkeyId;
        if (!RegisterHotKey(IntPtr.Zero, candidateId, hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey))
        {
            LastErrorCode = Marshal.GetLastWin32Error();
            return false;
        }

        if (_registered)
        {
            UnregisterHotKey(IntPtr.Zero, _registeredHotkeyId);
        }
        _registeredHotkeyId = candidateId;
        _currentHotkey = hotkey;
        LastErrorCode = 0;
        _registered = true;
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

    private sealed record ChangeRequest(HotkeyGesture Hotkey)
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

public sealed class HotkeyPressedEventArgs(IntPtr foregroundWindow, uint triggerKey = 0x54) : EventArgs
{
    public IntPtr ForegroundWindow { get; } = foregroundWindow;
    public uint TriggerKey { get; } = triggerKey;
}

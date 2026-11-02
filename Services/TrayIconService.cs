using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace QuickTranslate.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8052;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint OpenCommand = 1;
    private const uint SettingsCommand = 2;
    private const uint ExitCommand = 3;
    private static readonly UIntPtr SubclassId = new(0x5154);

    private readonly IntPtr _windowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SubclassProcedure _subclassProcedure;
    private readonly uint _taskbarCreatedMessage;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private bool _subclassInstalled;
    private bool _isAdded;
    private bool _usesVersion4;
    private bool _menuOpen;
    private bool _noticeShown;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? Unavailable;

    public bool IsAvailable => !_disposed && _isAdded;

    public TrayIconService(IntPtr windowHandle, DispatcherQueue dispatcherQueue)
    {
        _windowHandle = windowHandle;
        _dispatcherQueue = dispatcherQueue;
        _subclassProcedure = WindowProcedure;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            1, 0, 0, 0x0010 | 0x0040);
        _ownsIcon = _icon != IntPtr.Zero;
        if (!_ownsIcon)
        {
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        _subclassInstalled = SetWindowSubclass(_windowHandle, _subclassProcedure, SubclassId, UIntPtr.Zero);
        EnsureVisible();
    }

    public bool EnsureVisible()
    {
        if (_disposed || !_subclassInstalled || _icon == IntPtr.Zero)
        {
            return false;
        }

        var data = CreateIconData();
        if (_isAdded && ShellNotifyIcon(NimModify, ref data))
        {
            return true;
        }

        _isAdded = ShellNotifyIcon(NimAdd, ref data);
        if (_isAdded)
        {
            data.TimeoutOrVersion = 4;
            _usesVersion4 = ShellNotifyIcon(NimSetVersion, ref data);
        }
        return _isAdded;
    }

    public void ShowBackgroundNotice()
    {
        if (_noticeShown || !IsAvailable)
        {
            return;
        }
        _noticeShown = true;
        ShowNotice("QuickTranslate 正在后台运行",
            "全局快捷键仍可用。点击托盘图标可打开主窗口；右键菜单可打开设置或退出。");
    }

    public void ShowNotice(string title, string message)
    {
        if (!IsAvailable)
        {
            return;
        }

        var data = CreateIconData();
        data.Flags = NifInfo;
        data.InfoTitle = title;
        data.Info = message;
        data.InfoFlags = 1;
        ShellNotifyIcon(NimModify, ref data);
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmNcDestroy)
        {
            RemoveIcon();
            RemoveWindowSubclass(window, _subclassProcedure, SubclassId);
            _subclassInstalled = false;
        }
        else if (!_disposed && _taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            // Explorer discards all notification icons when its taskbar is recreated.
            _isAdded = false;
            if (!EnsureVisible())
            {
                QueueEvent(() => Unavailable?.Invoke(this, EventArgs.Empty));
            }
        }
        else if (!_disposed && message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            if (notification is NinSelect or NinKeySelect or WmLeftButtonDoubleClick ||
                (!_usesVersion4 && notification == WmLeftButtonUp))
            {
                QueueEvent(() => OpenRequested?.Invoke(this, EventArgs.Empty));
            }
            else if (notification == WmContextMenu || (!_usesVersion4 && notification == WmRightButtonUp))
            {
                var coordinates = wParam.ToUInt64();
                var position = new NativePoint
                {
                    X = (short)(coordinates & 0xFFFF),
                    Y = (short)((coordinates >> 16) & 0xFFFF)
                };
                if (!_usesVersion4 || (position.X == -1 && position.Y == -1))
                {
                    GetCursorPos(out position);
                }
                ShowContextMenu(position);
            }
            return IntPtr.Zero;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu(NativePoint position)
    {
        if (_menuOpen)
        {
            return;
        }
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        _menuOpen = true;
        uint command;
        try
        {
            AppendMenu(menu, 0, new UIntPtr(OpenCommand), "打开 QuickTranslate");
            AppendMenu(menu, 0, new UIntPtr(SettingsCommand), "设置");
            AppendMenu(menu, 0x0800, UIntPtr.Zero, null);
            AppendMenu(menu, 0, new UIntPtr(ExitCommand), "退出");
            SetMenuDefaultItem(menu, OpenCommand, false);
            SetForegroundWindow(_windowHandle);
            command = TrackPopupMenuEx(menu, 0x0100 | 0x0080 | 0x0002,
                position.X, position.Y, _windowHandle, IntPtr.Zero);
            PostMessage(_windowHandle, 0, UIntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
            _menuOpen = false;
        }

        // Defer window changes until the native menu and subclass callback have returned.
        switch (command)
        {
            case OpenCommand:
                QueueEvent(() => OpenRequested?.Invoke(this, EventArgs.Empty));
                break;
            case SettingsCommand:
                QueueEvent(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
                break;
            case ExitCommand:
                QueueEvent(() => ExitRequested?.Invoke(this, EventArgs.Empty));
                break;
        }
    }

    private void QueueEvent(Action action) => _dispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed)
        {
            action();
        }
    });

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _windowHandle,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "QuickTranslate · 点击打开，右键退出",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private void RemoveIcon()
    {
        if (_isAdded)
        {
            var data = CreateIconData();
            ShellNotifyIcon(NimDelete, ref data);
            _isAdded = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        RemoveIcon();
        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            _subclassInstalled = false;
        }
        if (_ownsIcon)
        {
            DestroyIcon(_icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure procedure, UIntPtr id, UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}

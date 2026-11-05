using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using QuickTranslate.Models;
using QuickTranslate.Services;
using QuickTranslate.Controls;
using System.Runtime.InteropServices;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace QuickTranslate;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly GlobalHotkeyService _globalHotkey;
    private readonly SelectionCaptureService _selectionCapture;
    private readonly MagpieTranslationService _translator;
    private readonly MainPage _mainPage;
    private readonly TrayIconService _trayIcon;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private TranslationWindow? _translationWindow;
    private QuickAskWindow? _quickAskWindow;
    private bool _selectionCaptureInProgress;
    private bool _isClosed;
    private bool _exitRequested;
    private bool _isHiddenToTray;
    private bool _navigationPillReady;
    private SegmentButton? _selectedNavigationItem;

    public bool IsTrayAvailable => _trayIcon.IsAvailable;

    public MainWindow()
    {
        InitializeComponent();

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _globalHotkey = App.Current.GlobalHotkey;
        _selectionCapture = App.Current.SelectionCapture;
        _translator = App.Current.Translator;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _globalHotkey.HotkeyPressed += OnHotkeyPressed;
        var startupSettings = App.Current.Settings.Load();
        _globalHotkey.Start(HotkeyGesture.FromSettings(startupSettings), HotkeyGesture.FromAskSettings(startupSettings));
        RootFrame.Navigate(typeof(MainPage));
        _mainPage = (MainPage)RootFrame.Content;
        SelectSegment(WorkbenchNavigationItem);
        NavigateTo("workbench");
        ApplyWindowMetrics();
        _trayIcon = new TrayIconService(WindowNative.GetWindowHandle(this), _dispatcherQueue);
        _trayIcon.OpenRequested += OnTrayOpenRequested;
        _trayIcon.SettingsRequested += OnTraySettingsRequested;
        _trayIcon.ExitRequested += OnTrayExitRequested;
        _trayIcon.Unavailable += OnTrayUnavailable;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        NavigationTrackGrid.Loaded += OnNavigationTrackLoaded;
        NavigationTrackGrid.SizeChanged += OnNavigationTrackSizeChanged;
    }

    public void ShowMainWindow(bool openSettings = false)
    {
        if (_isClosed)
        {
            return;
        }

        if (openSettings)
        {
            SelectSegment(SettingsNavigationItem);
            NavigateTo("settings");
        }
        AppWindow.Show();
        _isHiddenToTray = false;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        ForceForeground();
        Activate();
    }

    private void ForceForeground()
    {
        var window = WindowNative.GetWindowHandle(this);
        var foreground = GetForegroundWindow();
        if (foreground == window)
        {
            return;
        }

        // Activation requests arrive from a second process (single-instance redirect) or
        // from the tray; the foreground lock can otherwise leave the window behind others.
        var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            _ = SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private void OnTrayOpenRequested(object? sender, EventArgs e) => ShowMainWindow();

    private void OnTraySettingsRequested(object? sender, EventArgs e) => ShowMainWindow(openSettings: true);

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        _exitRequested = true;
        Close();
    }

    private void OnTrayUnavailable(object? sender, EventArgs e)
    {
        ShowMainWindow();
        _mainPage.SetStatus("托盘图标暂时不可用，已恢复主窗口，避免应用隐藏后无法找回。", isError: true);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested || _isClosed || !App.Current.Settings.Load().MinimizeToTrayOnClose)
        {
            return;
        }
        // Never hide the only way back into the app if the notification icon is unavailable.
        if (!_trayIcon.EnsureVisible())
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
        _isHiddenToTray = true;
        TrimWorkingSetWhenIdle();
        _trayIcon.ShowBackgroundNotice();
    }

    // WinUI keeps composed surfaces and managed caches after a window is hidden; hand the pages
    // back to Windows once the app has been sitting in the tray for a while.
    private void TrimWorkingSetWhenIdle()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(40), _lifetimeCancellation.Token);
                if (_isClosed || !_isHiddenToTray)
                {
                    return;
                }

                GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        });
    }

    private void NavSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not SegmentButton { Tag: string tag } segment)
        {
            return;
        }

        SelectSegment(segment);
        NavigateTo(tag);
    }

    private void SelectSegment(SegmentButton selected)
    {
        _selectedNavigationItem = selected;
        foreach (var segment in new[] { WorkbenchNavigationItem, HistoryNavigationItem, SettingsNavigationItem })
        {
            segment.IsSelected = ReferenceEquals(segment, selected);
        }

        UpdateNavigationPill(selected, animate: _navigationPillReady);
    }

    private void OnNavigationTrackLoaded(object sender, RoutedEventArgs e)
    {
        _navigationPillReady = true;
        UpdateNavigationPill(_selectedNavigationItem ?? WorkbenchNavigationItem, animate: false);
    }

    private void OnNavigationTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_navigationPillReady)
        {
            UpdateNavigationPill(_selectedNavigationItem ?? WorkbenchNavigationItem, animate: false);
        }
    }

    private void UpdateNavigationPill(SegmentButton selected, bool animate)
    {
        if (!_navigationPillReady || selected.ActualWidth <= 0 || NavigationTrackGrid.ActualHeight <= 0)
        {
            return;
        }

        var position = selected.TransformToVisual(NavigationTrackGrid).TransformPoint(new Point(0, 0));
        var targetOffset = new Vector3((float)position.X, 0, 0);
        var targetSize = new Vector2((float)selected.ActualWidth, (float)NavigationTrackGrid.ActualHeight);
        var visual = ElementCompositionPreview.GetElementVisual(NavigationSelectionPill);
        NavigationSelectionPill.Width = selected.ActualWidth;
        NavigationSelectionPill.Height = NavigationTrackGrid.ActualHeight;
        NavigationSelectionPill.Opacity = 1;

        if (!animate)
        {
            visual.StopAnimation("Offset");
            visual.StopAnimation("Size");
            visual.Offset = targetOffset;
            visual.Size = targetSize;
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.18f, 0.82f),
            new Vector2(0.22f, 1f));
        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.InsertKeyFrame(0, visual.Offset);
        offsetAnimation.InsertKeyFrame(1, targetOffset, easing);
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(210);
        visual.StartAnimation("Offset", offsetAnimation);

        var sizeAnimation = compositor.CreateVector2KeyFrameAnimation();
        sizeAnimation.InsertKeyFrame(0, visual.Size);
        sizeAnimation.InsertKeyFrame(1, targetSize, easing);
        sizeAnimation.Duration = TimeSpan.FromMilliseconds(210);
        visual.StartAnimation("Size", sizeAnimation);
    }

    private void NavigateTo(string tag)
    {
        if (RootFrame is null)
        {
            return;
        }

        var destination = tag switch
        {
            "settings" => typeof(SettingsPage),
            "history" => typeof(HistoryPage),
            _ => typeof(MainPage)
        };
        if (RootFrame.CurrentSourcePageType != destination)
        {
            RootFrame.Navigate(destination);
        }
    }

    private void ApplyWindowMetrics()
    {
        try
        {
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var scale = GetWindowScale();
            var width = Math.Min((int)Math.Round(880 * scale), workArea.Width - 64);
            var height = Math.Min((int)Math.Round(600 * scale), workArea.Height - 64);
            AppWindow.MoveAndResize(new RectInt32(
                workArea.X + ((workArea.Width - width) / 2),
                workArea.Y + ((workArea.Height - height) / 2),
                width,
                height));
            AppWindow.Changed += KeepMinimumSize;
            RoundCorners();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
        }
    }

    private void KeepMinimumSize(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _isClosed)
        {
            return;
        }

        var scale = GetWindowScale();
        var minWidth = (int)Math.Round(720 * scale);
        var minHeight = (int)Math.Round(500 * scale);
        var size = sender.Size;
        var width = Math.Max(size.Width, minWidth);
        var height = Math.Max(size.Height, minHeight);
        if (width != size.Width || height != size.Height)
        {
            sender.Resize(new SizeInt32(width, height));
        }
    }

    private void RoundCorners()
    {
        const int DwmwaWindowCornerPreference = 33;
        const int DwmwcpRound = 2;
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(WindowNative.GetWindowHandle(this), DwmwaWindowCornerPreference,
            ref preference, Marshal.SizeOf<int>());
    }

    private double GetWindowScale() =>
        Math.Max(96, GetDpiForWindow(WindowNative.GetWindowHandle(this))) / 96d;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumSize, IntPtr maximumSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private async Task TranslateSelectedTextAsync(IntPtr sourceWindow, uint triggerKey)
    {
        if (_selectionCaptureInProgress || _isClosed)
        {
            return;
        }

        _selectionCaptureInProgress = true;
        var cancellationToken = _lifetimeCancellation.Token;
        var page = _mainPage;
        TranslationWindow? popup = null;
        string? selectedText = null;
        CancellationToken sessionToken = default;
        try
        {
            popup = EnsureTranslationWindow();
            popup.PrepareForSelection(sourceWindow, App.Current.Settings.Load());
            sessionToken = popup.SessionToken;
            popup.ShowWithoutActivation();

            using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken);
            // Yield so the loading surface can render while the shortcut keys are being released.
            await Task.Delay(1, captureCancellation.Token);
            selectedText = await _selectionCapture.CaptureSelectedTextAsync(sourceWindow, captureCancellation.Token, triggerKey);
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                var error = $"未能复制选中文字，不会使用剪贴板旧内容。请确认文字可以用 Ctrl+C 复制，再按 {_globalHotkey.CurrentHotkey.DisplayText}。";
                popup.ShowCaptureError(error);
                popup.Activate();
                page.SetStatus(error, isError: true);
                return;
            }

            captureCancellation.Token.ThrowIfCancellationRequested();
            popup.SetSourceText(selectedText);
            // Selection capture and clipboard restoration have finished, so focus is now safe to move.
            popup.Activate();
            page.SetSourceText(selectedText);
            page.SetStatus("正在请求 Magpie…");
        }
        catch (OperationCanceledException) when (!_isClosed)
        {
            page.SetStatus("读取选区已取消。");
            return;
        }
        catch (ProtectedWindowCaptureException ex) when (!_isClosed)
        {
            page.SetStatus(ex.Message, isError: true);
            if (popup is { IsClosed: false })
            {
                popup.ShowCaptureError(ex.Message, showElevationAction: true);
                popup.Activate();
            }

            if (App.Current.Settings.Load().AutoElevateForProtectedApps)
            {
                if (popup is { IsClosed: false })
                {
                    popup.ShowCaptureError($"{ex.Message}\n\n正在请求管理员权限…", showElevationAction: true);
                }

                var restart = await App.Current.RequestElevatedRestartAsync();
                if (restart.Started)
                {
                    return;
                }

                var message = $"{ex.Message}\n{restart.Error}";
                page.SetStatus(message, isError: true);
                if (popup is { IsClosed: false })
                {
                    popup.ShowCaptureError(message, showElevationAction: true);
                }
            }

            return;
        }
        catch (Exception ex) when (!_isClosed)
        {
            page.SetStatus(ex.Message, isError: true);
            if (popup is { IsClosed: false })
            {
                popup.ShowCaptureError(ex.Message);
                popup.Activate();
            }
            return;
        }
        catch (Exception) when (_isClosed)
        {
            return;
        }
        finally
        {
            _selectionCaptureInProgress = false;
        }

        if (popup is null || popup.IsClosed || _isClosed || sessionToken.IsCancellationRequested)
        {
            return;
        }

        await popup.TranslateAsync(selectedText!, sessionToken);
    }

    public void ExitForRestart()
    {
        if (_isClosed)
        {
            return;
        }

        _exitRequested = true;
        Close();
    }

    public void ShowElevatedRestartNotice() =>
        _trayIcon.ShowNotice("已切换到管理员模式",
            "现在可以读取管理员窗口中的选区。请重新选中文字并按快捷键。");

    private TranslationWindow EnsureTranslationWindow()
    {
        if (_translationWindow is { IsClosed: false } existing)
        {
            return existing;
        }

        var popup = new TranslationWindow(App.Current.Settings.Load(), _translator);
        popup.TranslationStateChanged += OnTranslationStateChanged;
        popup.FreeformAskRequested += OnFreeformAskRequested;
        _translationWindow = popup;
        return popup;
    }

    private void OnFreeformAskRequested(object? sender, EventArgs e)
    {
        if (sender is TranslationWindow popup)
        {
            popup.HideForReuse();
        }
        ShowQuickAsk(IntPtr.Zero);
    }

    private void ShowQuickAsk(IntPtr sourceWindow)
    {
        if (_isClosed)
        {
            return;
        }

        var window = _quickAskWindow is { IsClosed: false } existing
            ? existing
            : CreateQuickAskWindow();
        window.PrepareForOpen(App.Current.Settings.Load(), sourceWindow);
        window.ShowAndFocus();
    }

    public void ShowQuickAskForExternalRequest() => ShowQuickAsk(IntPtr.Zero);

    private QuickAskWindow CreateQuickAskWindow()
    {
        var window = new QuickAskWindow(App.Current.Settings.Load(), _translator);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_quickAskWindow, window))
            {
                _quickAskWindow = null;
            }
        };
        _quickAskWindow = window;
        return window;
    }

    private void OnTranslationStateChanged(object? sender, EventArgs e)
    {
        if (_isClosed || sender is not TranslationWindow popup || popup.IsClosed || !ReferenceEquals(_translationWindow, popup))
        {
            return;
        }

        var page = _mainPage;
        if (page.SourceText != popup.SourceText)
        {
            return;
        }
        if (popup.IsTranslating)
        {
            page.SetResultText(string.Empty);
            page.SetStatus("正在请求 Magpie…");
        }
        else if (popup.LastError is not null)
        {
            page.SetStatus(popup.LastError, isError: true);
        }
        else if (!string.IsNullOrEmpty(popup.ResultText))
        {
            page.SetResultText(popup.ResultText);
            page.SetStatus($"{popup.ActionName}完成。");
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (e.Action == HotkeyAction.QuickAsk)
            {
                ShowQuickAsk(e.ForegroundWindow);
            }
            else
            {
                _ = TranslateSelectedTextAsync(e.ForegroundWindow, e.TriggerKey);
            }
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }
        _isClosed = true;
        AppWindow.Closing -= OnClosing;
        _trayIcon.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _globalHotkey.HotkeyPressed -= OnHotkeyPressed;
        _globalHotkey.Dispose();
        _translationWindow?.CloseForShutdown();
        _quickAskWindow?.CloseForShutdown();
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuickTranslate.Models;
using QuickTranslate.Services;
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
    private bool _selectionCaptureInProgress;
    private bool _isClosed;
    private bool _exitRequested;

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
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _globalHotkey.HotkeyPressed += OnHotkeyPressed;
        _globalHotkey.Start(HotkeyGesture.FromSettings(App.Current.Settings.Load()));
        RootFrame.Navigate(typeof(MainPage));
        _mainPage = (MainPage)RootFrame.Content;
        ShellNavigation.SelectedItem = WorkbenchNavigationItem;
        _trayIcon = new TrayIconService(WindowNative.GetWindowHandle(this), _dispatcherQueue);
        _trayIcon.OpenRequested += OnTrayOpenRequested;
        _trayIcon.SettingsRequested += OnTraySettingsRequested;
        _trayIcon.ExitRequested += OnTrayExitRequested;
        _trayIcon.Unavailable += OnTrayUnavailable;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
    }

    public void ShowMainWindow(bool openSettings = false)
    {
        if (_isClosed)
        {
            return;
        }

        if (openSettings)
        {
            ShellNavigation.SelectedItem = SettingsNavigationItem;
        }
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
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
        _trayIcon.ShowBackgroundNotice();
    }

    private void ShellNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (RootFrame is null || args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var destination = item.Tag as string == "settings" ? typeof(SettingsPage) : typeof(MainPage);
        if (RootFrame.CurrentSourcePageType != destination)
        {
            RootFrame.Navigate(destination);
        }
    }

    private async Task TranslateSelectedTextAsync(IntPtr sourceWindow, uint triggerKey)
    {
        if (_selectionCaptureInProgress || _isClosed)
        {
            return;
        }

        _selectionCaptureInProgress = true;
        var cancellationToken = _lifetimeCancellation.Token;
        var page = _mainPage;
        var previousWindow = _translationWindow;
        var previousRequest = previousWindow?.PendingRequest;
        var selectionIsInPreviousWindow = previousWindow is not null &&
            WindowNative.GetWindowHandle(previousWindow) == sourceWindow;
        TranslationWindow? popup = null;
        string? selectedText = null;
        try
        {
            if (!selectionIsInPreviousWindow)
            {
                previousWindow?.Close();
            }

            popup = new TranslationWindow(App.Current.Settings.Load(), _translator, previousRequest, sourceWindow);
            _translationWindow = popup;
            popup.TranslationStateChanged += OnTranslationStateChanged;
            popup.Closed += (_, _) =>
            {
                popup.TranslationStateChanged -= OnTranslationStateChanged;
                if (ReferenceEquals(_translationWindow, popup))
                {
                    _translationWindow = null;
                }
            };
            popup.ShowWithoutActivation();

            using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, popup.LifetimeToken);
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
            // Keep an existing popup alive until copying finishes if it is itself the source window.
            if (selectionIsInPreviousWindow && previousWindow is { IsClosed: false })
            {
                previousWindow.Close();
            }
            _selectionCaptureInProgress = false;
        }

        if (popup is null || popup.IsClosed || _isClosed)
        {
            return;
        }

        await popup.TranslateAsync(selectedText!, cancellationToken);
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
        _dispatcherQueue.TryEnqueue(() => _ = TranslateSelectedTextAsync(e.ForegroundWindow, e.TriggerKey));
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
        _translationWindow?.Close();
    }
}

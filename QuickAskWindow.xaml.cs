using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Windows.Graphics;
using Windows.UI.Core;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace QuickTranslate;

/// <summary>
/// Free-form assistant that never reads the selection. Opened by its own global shortcut
/// so it can be typed into immediately, and reused between invocations.
/// </summary>
public sealed partial class QuickAskWindow : Window
{
    private const int MaxHistoryTurns = 10;

    private readonly MagpieTranslationService _translator;
    private readonly TargetLanguageService _targetLanguage;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ObservableCollection<ConversationTurn> _conversation = [];
    private readonly ThemeSettings _themeSettings;
    private AppSettings _settings;
    private CancellationTokenSource? _activeCancellation;
    private bool _isSending;
    private bool _syncingModel;
    private bool _isRefreshingModels;
    private bool _modelsLoaded;
    private bool _isComposing;
    private long _compositionEndedAt = -80;
    private bool _allowClose;

    public bool IsClosed { get; private set; }
    public CancellationToken LifetimeToken { get; }

    public QuickAskWindow(AppSettings settings, MagpieTranslationService translator)
    {
        _translator = translator;
        _targetLanguage = App.Current.TargetLanguage;
        _settings = CloneSettings(settings);
        LifetimeToken = _lifetimeCancellation.Token;

        InitializeComponent();
        _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        ConversationItems.ItemsSource = _conversation;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(PopupTitleBar);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        ApplyCaptionColors();
        RoundCorners();
        SyncModelSelection();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        Activated += OnWindowActivated;
        _themeSettings.Changed += ThemeSettings_Changed;
        App.Current.Settings.Saved += Settings_Saved;
        Closed += OnClosed;
    }

    public void PrepareForOpen(AppSettings settings, IntPtr sourceWindow)
    {
        if (IsClosed)
        {
            return;
        }

        _settings = CloneSettings(settings);
        _settings.TargetLanguage = _targetLanguage.CurrentLanguage;
        SyncModelSelection();
        if (!_modelsLoaded)
        {
            _ = RefreshModelsAsync(_settings.ModelId);
        }
        AskErrorBar.IsOpen = string.IsNullOrWhiteSpace(_settings.ModelId);
        AskErrorBar.Message = "还没有选择模型。请先到设置页连接 Magpie 并选择一个模型。";
        EmptyStatePanel.Visibility = _conversation.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceAtTopRight(sourceWindow);
    }

    public void ShowAndFocus()
    {
        if (IsClosed)
        {
            return;
        }

        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        ForceForeground();
        Activate();
        _ = FocusComposerAsync();
    }

    private void ForceForeground()
    {
        var window = WindowNative.GetWindowHandle(this);
        var foreground = GetForegroundWindow();
        if (foreground == window)
        {
            return;
        }

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

    private async Task FocusComposerAsync()
    {
        // Activation completes asynchronously, so retry briefly instead of focusing once too early.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(60);
            if (IsClosed)
            {
                return;
            }

            if (QuestionTextBox.Focus(FocusState.Programmatic))
            {
                QuestionTextBox.SelectionStart = QuestionTextBox.Text.Length;
                return;
            }
        }
    }

    private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && !IsClosed)
        {
            _ = FocusComposerAsync();
        }
    }

    private void Settings_Saved(object? sender, EventArgs e)
    {
        if (IsClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsClosed)
            {
                return;
            }

            _settings = CloneSettings(App.Current.Settings.Load());
            _settings.TargetLanguage = _targetLanguage.CurrentLanguage;
            SyncModelSelection();
        });
    }

    private void SyncModelSelection()
    {
        _syncingModel = true;
        try
        {
            var items = ModelComboBox.Items.OfType<MagpieModel>().ToList();
            var selected = items.FirstOrDefault(model =>
                model.Id.Equals(_settings.ModelId, StringComparison.OrdinalIgnoreCase));
            if (selected is null && !string.IsNullOrWhiteSpace(_settings.ModelId))
            {
                // Keep a saved model visible even when Magpie has not answered yet
                // or no longer lists it (for example after renaming a route).
                selected = new MagpieModel(_settings.ModelId, $"{_settings.ModelId}（当前）", "unknown");
                items.Insert(0, selected);
                ModelComboBox.ItemsSource = items;
            }
            ModelComboBox.SelectedItem = selected;
        }
        finally
        {
            _syncingModel = false;
        }
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingModel || IsClosed || ModelComboBox.SelectedItem is not MagpieModel model)
        {
            return;
        }

        _settings.ModelId = model.Id;
        try
        {
            var settings = App.Current.Settings.Load();
            settings.ModelId = model.Id;
            App.Current.Settings.Save(settings);
            AskErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            AskErrorBar.IsOpen = true;
            AskErrorBar.Message = $"模型已切换，但未能保存：{ex.Message}";
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) =>
        await RefreshModelsAsync(ModelComboBox.SelectedValue as string ?? _settings.ModelId);

    private async Task RefreshModelsAsync(string? preferredModel)
    {
        if (_isRefreshingModels || IsClosed)
        {
            return;
        }

        _isRefreshingModels = true;
        RefreshModelsButton.IsEnabled = false;
        try
        {
            var models = await _translator.GetModelsAsync(_settings.MagpieBaseUrl, LifetimeToken);
            _modelsLoaded = true;
            _syncingModel = true;
            ModelComboBox.ItemsSource = models;
            var selected = models.FirstOrDefault(model =>
                model.Id.Equals(preferredModel, StringComparison.OrdinalIgnoreCase));
            ModelComboBox.SelectedItem = selected;
            _syncingModel = false;
            SyncModelSelection();
            AskErrorBar.IsOpen = models.Count == 0;
            if (models.Count == 0)
            {
                AskErrorBar.Message = "Magpie 未返回可用模型。请确认 Magpie 正在运行并已配置模型。";
            }
        }
        catch (Exception ex)
        {
            _syncingModel = false;
            AskErrorBar.IsOpen = true;
            AskErrorBar.Message = ex.Message;
        }
        finally
        {
            _isRefreshingModels = false;
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        var question = QuestionTextBox.Text.Trim();
        if (_isSending || string.IsNullOrWhiteSpace(question) || IsClosed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ModelId))
        {
            AskErrorBar.IsOpen = true;
            AskErrorBar.Message = "还没有选择模型。请先到设置页连接 Magpie 并选择一个模型。";
            return;
        }

        AskErrorBar.IsOpen = false;
        var turn = new ConversationTurn(question);
        _conversation.Add(turn);
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        QuestionTextBox.Text = string.Empty;
        await AnswerAsync(turn);
        ScrollToLatest();
        _ = FocusComposerAsync();
    }

    private async Task AnswerAsync(ConversationTurn turn)
    {
        _isSending = true;
        turn.Start();
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        UpdateComposerState();

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
        _activeCancellation = requestCancellation;
        var token = requestCancellation.Token;
        try
        {
            var messages = BuildMessages(turn);
            var settings = CloneSettings(_settings);
            var builder = new StringBuilder();
            var lastPreviewAt = 0L;
            if (settings.EnableStreaming)
            {
                await foreach (var chunk in _translator.StreamChatAsync(messages, settings, token)
                                   .WithCancellation(token))
                {
                    token.ThrowIfCancellationRequested();
                    builder.Append(chunk);
                    var now = Environment.TickCount64;
                    if (now - lastPreviewAt >= 80)
                    {
                        turn.UpdateAnswer(builder.ToString());
                        lastPreviewAt = now;
                    }
                }
            }
            else
            {
                builder.Append(await _translator.ChatAsync(messages, settings, token));
            }

            token.ThrowIfCancellationRequested();
            var answer = builder.ToString().Trim();
            if (answer.Length == 0)
            {
                throw new InvalidOperationException("Magpie 返回了空回答。");
            }

            turn.Complete(answer);
        }
        catch (OperationCanceledException)
        {
            turn.Fail(IsClosed ? "已关闭" : "已取消，可以重新发送。");
        }
        catch (Exception ex)
        {
            turn.Fail(ex.Message);
        }
        finally
        {
            _activeCancellation = null;
            if (!IsClosed)
            {
                _isSending = false;
                CancelButton.Visibility = Visibility.Collapsed;
                UpdateComposerState();
            }
        }
    }

    private List<ChatMessage> BuildMessages(ConversationTurn current)
    {
        var messages = new List<ChatMessage>();
        foreach (var turn in _conversation.Where(item => item != current && !string.IsNullOrEmpty(item.Answer))
                     .TakeLast(MaxHistoryTurns))
        {
            messages.Add(new ChatMessage("user", turn.Question));
            messages.Add(new ChatMessage("assistant", turn.Answer));
        }
        messages.Add(new ChatMessage("user", current.Question));
        return messages;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _activeCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void RetryQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSending || IsClosed || sender is not FrameworkElement { DataContext: ConversationTurn turn })
        {
            return;
        }

        await AnswerAsync(turn);
        ScrollToLatest();
    }

    private void NewConversationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _activeCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _conversation.Clear();
        EmptyStatePanel.Visibility = Visibility.Visible;
        AskErrorBar.IsOpen = false;
        UpdateComposerState();
        _ = FocusComposerAsync();
    }

    private void QuestionTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposerState();

    private void QuestionTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!IsClosed)
        {
            ComposerFocusRing.Opacity = 1;
        }
    }

    private void QuestionTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsClosed)
        {
            ComposerFocusRing.Opacity = 0;
        }
    }

    private void UpdateComposerState()
    {
        if (IsClosed)
        {
            return;
        }

        SendButton.IsEnabled = !_isSending && !string.IsNullOrWhiteSpace(QuestionTextBox.Text);
    }

    private async void QuestionTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter ||
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0 ||
            _isComposing || Environment.TickCount64 - _compositionEndedAt is >= 0 and < 80)
        {
            return;
        }

        e.Handled = true;
        await SendAsync();
    }

    private void QuestionTextBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) =>
        _isComposing = true;

    private void QuestionTextBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _isComposing = false;
        _compositionEndedAt = Environment.TickCount64;
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !_isComposing)
        {
            e.Handled = true;
            HideForReuse();
        }
    }

    public void HideForReuse()
    {
        if (IsClosed)
        {
            return;
        }

        AppWindow.Hide();
    }

    public void CloseForShutdown()
    {
        if (IsClosed)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || IsClosed)
        {
            return;
        }

        // Keep the conversation for the next shortcut press; X only hides the panel.
        args.Cancel = true;
        HideForReuse();
    }

    private void ScrollToLatest()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsClosed)
            {
                BodyScroller.ChangeView(null, BodyScroller.ScrollableHeight, null, false);
                LatestButton.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void LatestButton_Click(object sender, RoutedEventArgs e) => ScrollToLatest();

    private void BodyScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (IsClosed)
        {
            return;
        }

        var remaining = BodyScroller.ScrollableHeight - BodyScroller.VerticalOffset;
        LatestButton.Visibility = remaining > 40 && _conversation.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var padding = e.NewSize.Width < 380 ? 12d : 16d;
        BodyScroller.Padding = new Thickness(padding, 0, padding, 14);
        ComposerBorder.Padding = new Thickness(padding, 10, padding, 12);
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args) => ApplyCaptionColors();

    private void ApplyCaptionColors()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = _themeSettings.HighContrast
            ? null
            : RootGrid.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = null;
    }

    private void ThemeSettings_Changed(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed)
            {
                ApplyCaptionColors();
            }
        });

    private void PlaceAtTopRight(IntPtr sourceWindow)
    {
        _ = GetCursorPos(out var cursor);
        var display = sourceWindow != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(sourceWindow), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        // Move first so GetDpiForWindow reflects the target monitor's scale.
        AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
        var scale = GetWindowScale();
        var margin = (int)Math.Round(14 * scale);
        var width = Math.Max(1, Math.Min((int)Math.Round(440 * scale), workArea.Width - margin * 2));
        var height = Math.Max(1, Math.Min((int)Math.Round(560 * scale), workArea.Height - margin * 2));
        AppWindow.MoveAndResize(new RectInt32(workArea.X + workArea.Width - width - margin,
            workArea.Y + margin, width, height));
    }

    private double GetWindowScale() => Math.Max(96, GetDpiForWindow(WindowNative.GetWindowHandle(this))) / 96d;

    private void RoundCorners()
    {
        const int DwmwaWindowCornerPreference = 33;
        const int DwmwcpRound = 2;
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(WindowNative.GetWindowHandle(this), DwmwaWindowCornerPreference,
            ref preference, Marshal.SizeOf<int>());
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || IsClosed)
        {
            return;
        }

        var workArea = DisplayArea.GetFromWindowId(sender.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetWindowScale();
        var width = Math.Max(sender.Size.Width, Math.Min((int)Math.Round(380 * scale), workArea.Width));
        var height = Math.Max(sender.Size.Height, Math.Min((int)Math.Round(380 * scale), workArea.Height));
        if (width != sender.Size.Width || height != sender.Size.Height)
        {
            sender.Resize(new SizeInt32(width, height));
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        Activated -= OnWindowActivated;
        _themeSettings.Changed -= ThemeSettings_Changed;
        App.Current.Settings.Saved -= Settings_Saved;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private static AppSettings CloneSettings(AppSettings settings) => new()
    {
        MagpieBaseUrl = settings.MagpieBaseUrl,
        ModelId = settings.ModelId,
        TargetLanguage = settings.TargetLanguage,
        TaskPrompt = settings.TaskPrompt,
        HotkeyModifiers = settings.HotkeyModifiers,
        HotkeyVirtualKey = settings.HotkeyVirtualKey,
        AskHotkeyModifiers = settings.AskHotkeyModifiers,
        AskHotkeyVirtualKey = settings.AskHotkeyVirtualKey,
        StartWithWindows = settings.StartWithWindows,
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
        EnableStreaming = settings.EnableStreaming,
        EnableTranslationCache = settings.EnableTranslationCache,
        EnableTranslationHistory = settings.EnableTranslationHistory,
        HistoryLimit = settings.HistoryLimit,
        ReasoningEffortByModel = new Dictionary<string, string>(
            settings.ReasoningEffortByModel ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
    };

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}

using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI.Core;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;

namespace QuickTranslate;

public sealed partial class TranslationWindow : Window
{
    private readonly MagpieTranslationService _translator;
    private readonly AppSettings _settings;
    private readonly TargetLanguageService _targetLanguage;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource _languageCancellation = new();
    private readonly ThemeSettings _themeSettings;
    private readonly ObservableCollection<ConversationTurn> _conversation = [];
    private Task _pendingRequest;
    private Task<string?>? _translationTask;
    private string _original = string.Empty;
    private string _translation = string.Empty;
    private bool _initialized;
    private bool _isTranslating = true;
    private bool _isAnswering;
    private bool _isComposing;
    private bool _originalExpanded;
    private long _compositionEndedAt = -80;
    private int _languageRevision;

    public CancellationToken LifetimeToken { get; }
    public Task PendingRequest => _pendingRequest;
    public bool IsClosed { get; private set; }
    public string? LastError { get; private set; }
    public string SourceText => _original;
    public string ResultText => _translation;
    public string ActionName => TaskPrompts.GetActionName(_settings.TaskPrompt);
    private string ResultLabel => TaskPrompts.GetResultLabel(_settings.TaskPrompt);
    public bool IsTranslating => _isTranslating;
    public event EventHandler? TranslationStateChanged;

    public TranslationWindow(AppSettings settings, MagpieTranslationService translator, Task? previousRequest = null,
        IntPtr sourceWindow = default)
    {
        _translator = translator;
        _targetLanguage = App.Current.TargetLanguage;
        _settings = new AppSettings
        {
            MagpieBaseUrl = settings.MagpieBaseUrl,
            ModelId = settings.ModelId,
            TargetLanguage = _targetLanguage.CurrentLanguage,
            TaskPrompt = settings.TaskPrompt,
            HotkeyModifiers = settings.HotkeyModifiers,
            HotkeyVirtualKey = settings.HotkeyVirtualKey
        };
        _pendingRequest = previousRequest ?? Task.CompletedTask;
        LifetimeToken = _lifetimeCancellation.Token;
        InitializeComponent();
        _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(PopupTitleBar);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        TargetLanguageComboBox.ItemsSource = _targetLanguage.Languages;
        TargetLanguageComboBox.SelectedItem = _targetLanguage.CurrentLanguage;
        ConversationItems.ItemsSource = _conversation;
        _initialized = true;
        UpdateTaskPresentation();
        ApplyCaptionColors();
        PlaceAtTopRight(sourceWindow);
        AppWindow.Changed += AppWindow_Changed;
        _themeSettings.Changed += ThemeSettings_Changed;
        _targetLanguage.Changed += TargetLanguage_Changed;
        App.Current.Settings.Saved += Settings_Saved;
        Closed += OnClosed;
    }

    private void UpdateTaskPresentation()
    {
        var title = ActionName == "处理" ? "划词助手" : $"划词{ActionName}";
        Title = $"{title} - QuickTranslate";
        PopupTitleBar.Title = title;
        LanguageCaptionTextBlock.Text = ActionName == "翻译" ? "译为" : "回答语言";
        ResultHeadingTextBlock.Text = ResultLabel;
        CopyButton.Label = $"复制{ResultLabel}";
        ToolTipService.SetToolTip(CopyButton, $"复制{ResultLabel}");
        RetryTranslationButton.Label = $"重新{ActionName}";
        ToolTipService.SetToolTip(RetryTranslationButton, $"重新{ActionName}");
        ModelTextBlock.Text = _settings.ModelId.Split('/').LastOrDefault() ?? _settings.ModelId;
        ToolTipService.SetToolTip(ModelTextBlock, _settings.ModelId);
        UpdateComposerState();
    }

    private void Settings_Saved(object? sender, EventArgs e)
    {
        if (IsClosed)
        {
            return;
        }

        var updated = App.Current.Settings.Load();
        if (updated.TaskPrompt == _settings.TaskPrompt && updated.ModelId == _settings.ModelId &&
            updated.MagpieBaseUrl == _settings.MagpieBaseUrl)
        {
            return;
        }

        _settings.TaskPrompt = updated.TaskPrompt;
        _settings.ModelId = updated.ModelId;
        _settings.MagpieBaseUrl = updated.MagpieBaseUrl;
        UpdateTaskPresentation();
        RestartProcessing("任务或模型已更新，请在新结果显示后重新发送。");
    }

    private void TargetLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || TargetLanguageComboBox.SelectedItem is not string language || language == _targetLanguage.CurrentLanguage)
        {
            return;
        }

        try
        {
            _targetLanguage.SetLanguage(language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TargetLanguageComboBox.SelectedItem = _targetLanguage.CurrentLanguage;
            TranslationErrorBar.Title = "未能保存目标语言";
            TranslationErrorBar.Message = ex.Message;
            TranslationErrorBar.IsOpen = true;
        }
    }

    private void TargetLanguage_Changed(object? sender, EventArgs e)
    {
        if (IsClosed)
        {
            return;
        }

        _settings.TargetLanguage = _targetLanguage.CurrentLanguage;
        TargetLanguageComboBox.SelectedItem = _targetLanguage.CurrentLanguage;
        RestartProcessing("目标语言已切换，请在新结果显示后重新发送。");
    }

    private void RestartProcessing(string cancellationMessage)
    {
        _languageRevision++;
        var previousCancellation = _languageCancellation;
        _languageCancellation = new CancellationTokenSource();
        previousCancellation.Cancel();
        previousCancellation.Dispose();
        _translationTask = null;
        _isAnswering = false;
        foreach (var turn in _conversation.Where(turn => turn.IsPending))
        {
            turn.Fail(cancellationMessage);
        }

        if (!string.IsNullOrEmpty(_original))
        {
            _ = TranslateAsync(_original);
            BodyScroller.ChangeView(null, 0, null, true);
        }
    }

    private AppSettings SnapshotSettings() => new()
    {
        MagpieBaseUrl = _settings.MagpieBaseUrl,
        ModelId = _settings.ModelId,
        TargetLanguage = _settings.TargetLanguage,
        TaskPrompt = _settings.TaskPrompt,
        HotkeyModifiers = _settings.HotkeyModifiers,
        HotkeyVirtualKey = _settings.HotkeyVirtualKey
    };

    public void ShowWithoutActivation()
    {
        // Activating here would move focus away from the selection before Ctrl+C is sent.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.Show(false);
        // Raise within the topmost group without moving keyboard focus away from the source.
        AppWindow.MoveInZOrderAtTop();
    }

    public void SetSourceText(string original)
    {
        if (IsClosed)
        {
            return;
        }

        _original = original;
        _translation = string.Empty;
        TranslationTextBlock.Text = string.Empty;
        OriginalTextBlock.Text = original;
        CharacterCountTextBlock.Text = $"{original.Length:N0} 字符";
        LoadingTextBlock.Text = $"{ActionName}中…";
        LoadingHintTextBlock.Text = $"正在通过 Magpie {ActionName}选中文字，你也可以先写下想问的问题。";
        LoadingPanel.Visibility = Visibility.Visible;
        TranslationProgressRing.IsActive = true;
        TranslationTextBlock.Visibility = Visibility.Collapsed;
        TranslationErrorBar.IsOpen = false;
        RetryTranslationButton.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        LastError = null;
        _isTranslating = true;
        UpdateComposerState();
        TranslationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowCaptureError(string error)
    {
        if (IsClosed)
        {
            return;
        }

        OriginalTextBlock.Text = "没有读取到选中文字";
        ShowTranslationError("未能读取选区", error);
    }

    public Task<string?> TranslateAsync(string original, CancellationToken cancellationToken = default)
    {
        if (IsClosed)
        {
            return Task.FromResult<string?>(null);
        }
        if (_translationTask is { IsCompleted: false })
        {
            return _translationTask;
        }

        SetSourceText(original);
        var previousRequest = _pendingRequest;
        _translationTask = TranslateCoreAsync(original, SnapshotSettings(), previousRequest, _languageRevision,
            _languageCancellation.Token, cancellationToken);
        // Preserve the inherited request even if this window closes while waiting for it.
        _pendingRequest = previousRequest.IsCompleted ? _translationTask : Task.WhenAll(previousRequest, _translationTask);
        return _translationTask;
    }

    private async Task<string?> TranslateCoreAsync(string original, AppSettings settings, Task previousRequest,
        int revision, CancellationToken languageToken, CancellationToken cancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, languageToken, cancellationToken);
        var token = requestCancellation.Token;
        try
        {
            // A replacement popup cancels the old one, then waits for the shared service gate to release.
            try
            {
                await previousRequest.WaitAsync(token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // The previous window owns and displays its own request failure.
            }

            token.ThrowIfCancellationRequested();
            var result = await _translator.TranslateAsync(original, settings, token);
            token.ThrowIfCancellationRequested();
            _translation = result;
            TranslationTextBlock.Text = result;
            TranslationTextBlock.Visibility = Visibility.Visible;
            CopyButton.IsEnabled = true;
            return result;
        }
        catch (OperationCanceledException) when (IsClosed || revision != _languageRevision)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            ShowTranslationError($"{ActionName}已取消", "可以点击重试，或重新选择文字后按快捷键。");
            return null;
        }
        catch (Exception ex)
        {
            if (!IsClosed && revision == _languageRevision)
            {
                ShowTranslationError($"暂时无法{ActionName}", ex.Message);
            }
            return null;
        }
        finally
        {
            if (!IsClosed && revision == _languageRevision)
            {
                _isTranslating = false;
                LoadingPanel.Visibility = Visibility.Collapsed;
                TranslationProgressRing.IsActive = false;
                UpdateComposerState();
                TranslationStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ShowTranslationError(string title, string error)
    {
        LastError = error;
        _isTranslating = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        TranslationProgressRing.IsActive = false;
        TranslationErrorBar.Title = title;
        TranslationErrorBar.Message = error;
        TranslationErrorBar.IsOpen = true;
        RetryTranslationButton.Visibility = string.IsNullOrEmpty(_original) ? Visibility.Collapsed : Visibility.Visible;
        UpdateComposerState();
        TranslationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void RetryTranslationButton_Click(object sender, RoutedEventArgs e) => await TranslateAsync(_original);

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(_translation);
            Clipboard.SetContent(package);
            ComposerStatusTextBlock.Text = $"已复制{ResultLabel}";
            await Task.Delay(1600, LifetimeToken);
            UpdateComposerState();
        }
        catch (OperationCanceledException) when (IsClosed)
        {
        }
        catch (COMException)
        {
            if (!IsClosed)
            {
                ComposerStatusTextBlock.Text = "剪贴板正忙，请重试";
            }
        }
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e) => await AskAsync();

    private Task AskAsync()
    {
        var question = QuestionTextBox.Text.Trim();
        if (_isTranslating || _isAnswering || string.IsNullOrEmpty(_translation) || string.IsNullOrWhiteSpace(question) || IsClosed)
        {
            return Task.CompletedTask;
        }

        var turn = new ConversationTurn(question);
        _conversation.Add(turn);
        ConversationHeading.Visibility = Visibility.Visible;
        QuestionTextBox.Text = string.Empty;
        _pendingRequest = AnswerAsync(turn);
        ScrollToLatest();
        return _pendingRequest;
    }

    private async Task AnswerAsync(ConversationTurn turn)
    {
        var revision = _languageRevision;
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, _languageCancellation.Token);
        var token = requestCancellation.Token;
        _isAnswering = true;
        turn.Start();
        UpdateComposerState();
        try
        {
            var history = string.Join("\n\n", _conversation.TakeWhile(entry => entry != turn)
                .Where(entry => !entry.IsPending && !string.IsNullOrEmpty(entry.Answer))
                .Select(entry => $"你：{entry.Question}\n\n助手：{entry.Answer}"));
            var answer = await _translator.AskAsync(_original, _translation, history, turn.Question, SnapshotSettings(), token);
            token.ThrowIfCancellationRequested();
            var readingOffset = BodyScroller.VerticalOffset;
            turn.Complete(answer);
            // Changing the answer's height must not let native scroll anchoring move the reader.
            BodyScroller.UpdateLayout();
            BodyScroller.ChangeView(null, readingOffset, null, true);
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!IsClosed)
                {
                    BodyScroller.UpdateLayout();
                    UpdateLatestButtonVisibility();
                }
            });
        }
        catch (OperationCanceledException) when (IsClosed || revision != _languageRevision)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosed && revision == _languageRevision)
            {
                turn.Fail(ex is OperationCanceledException ? "请求已取消，可以重新发送。" : ex.Message);
            }
        }
        finally
        {
            if (!IsClosed && revision == _languageRevision)
            {
                _isAnswering = false;
                UpdateComposerState();
            }
        }
    }

    private async void RetryQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isTranslating && !_isAnswering && !IsClosed && sender is FrameworkElement { DataContext: ConversationTurn turn })
        {
            _pendingRequest = AnswerAsync(turn);
            await _pendingRequest;
        }
    }

    private void UpdateComposerState()
    {
        if (!_initialized || IsClosed)
        {
            return;
        }

        QuestionTextBox.IsEnabled = !string.IsNullOrEmpty(_original);
        AskButton.IsEnabled = !_isTranslating && !_isAnswering && !string.IsNullOrEmpty(_translation) &&
            !string.IsNullOrWhiteSpace(QuestionTextBox.Text);
        ComposerStatusTextBlock.Text = string.IsNullOrEmpty(_original)
            ? LastError is null ? "读取选区中" : "请重新划词后按快捷键"
            : _isTranslating ? $"{ActionName}中，可先输入问题"
            : _isAnswering ? "回答中，可继续输入"
            : string.IsNullOrEmpty(_translation) ? $"请先重试{ActionName}"
            : "结合原文与结果回答";
    }

    private void QuestionTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateComposerState();

    private async void QuestionTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter ||
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0 ||
            _isComposing || Environment.TickCount64 - _compositionEndedAt is >= 0 and < 80)
        {
            return;
        }

        e.Handled = true;
        await AskAsync();
    }

    private void QuestionTextBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _isComposing = true;

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
            if (TargetLanguageComboBox.IsDropDownOpen)
            {
                TargetLanguageComboBox.IsDropDownOpen = false;
                return;
            }
            Close();
        }
    }

    private void ExpandOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        _originalExpanded = !_originalExpanded;
        OriginalTextBlock.MaxLines = _originalExpanded ? 0 : 3;
        ExpandOriginalButton.Content = _originalExpanded ? "收起" : "展开";
    }

    private void OriginalTextBlock_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        ExpandOriginalButton.Visibility = _originalExpanded || sender.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToLatest()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsClosed)
            {
                BodyScroller.UpdateLayout();
                BodyScroller.ChangeView(null, BodyScroller.ScrollableHeight, null, true);
            }
        });
    }

    private void LatestButton_Click(object sender, RoutedEventArgs e) => ScrollToLatest();

    private void BodyScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        => UpdateLatestButtonVisibility();

    private void UpdateLatestButtonVisibility()
    {
        LatestButton.Visibility = _conversation.Count > 0 && BodyScroller.ScrollableHeight - BodyScroller.VerticalOffset > 80
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var padding = e.NewSize.Width < 420 ? 16 : 22;
        MetadataRow.Margin = new Thickness(padding, 6, padding, 14);
        BodyScroller.Padding = new Thickness(padding, 0, padding, 20);
        ComposerBorder.Padding = new Thickness(padding, 14, padding, 16);
        LatestButton.Margin = new Thickness(0, 0, padding, 12);
        KeyboardHintRow.Visibility = e.NewSize.Height < 480 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_initialized)
        {
            ApplyCaptionColors();
        }
    }

    private void ApplyCaptionColors()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = _themeSettings.HighContrast
            ? null : RootGrid.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = null;
    }

    private void ThemeSettings_Changed(ThemeSettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed)
            {
                ApplyCaptionColors();
            }
        });
    }

    private void PlaceAtTopRight(IntPtr sourceWindow)
    {
        _ = GetCursorPos(out var cursor);
        var display = sourceWindow != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(sourceWindow), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        // Moving first gives GetDpiForWindow the target monitor's scale, including secondary monitors.
        AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
        var scale = GetWindowScale();
        var margin = (int)Math.Round(16 * scale);
        var width = Math.Max(1, Math.Min((int)Math.Round(500 * scale), workArea.Width - margin * 2));
        var height = Math.Max(1, Math.Min((int)Math.Round(600 * scale), workArea.Height - margin * 2));
        AppWindow.MoveAndResize(new RectInt32(workArea.X + workArea.Width - width - margin,
            workArea.Y + margin, width, height));
    }

    private double GetWindowScale() => Math.Max(96, GetDpiForWindow(WindowNative.GetWindowHandle(this))) / 96d;

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || IsClosed)
        {
            return;
        }

        var workArea = DisplayArea.GetFromWindowId(sender.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetWindowScale();
        var width = Math.Max(sender.Size.Width, Math.Min((int)Math.Round(340 * scale), workArea.Width));
        var height = Math.Max(sender.Size.Height, Math.Min((int)Math.Round(420 * scale), workArea.Height));
        if (width != sender.Size.Width || height != sender.Size.Height)
        {
            sender.Resize(new SizeInt32(width, height));
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        AppWindow.Changed -= AppWindow_Changed;
        _themeSettings.Changed -= ThemeSettings_Changed;
        _targetLanguage.Changed -= TargetLanguage_Changed;
        App.Current.Settings.Saved -= Settings_Saved;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _languageCancellation.Cancel();
        _languageCancellation.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}

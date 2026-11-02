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
    private CancellationTokenSource? _activeRequestCancellation;
    private string _original = string.Empty;
    private string _translation = string.Empty;
    private string? _historyEntryId;
    private bool _initialized;
    private bool _isTranslating = true;
    private bool _isAnswering;
    private bool _isComposing;
    private bool _originalExpanded;
    private bool _allowClose;
    private long _compositionEndedAt = -80;
    private int _languageRevision;
    private bool _syncingPreset;

    public CancellationToken LifetimeToken { get; }
    public CancellationToken SessionToken => _languageCancellation.Token;
    public Task PendingRequest => _pendingRequest;
    public bool IsClosed { get; private set; }
    public string? LastError { get; private set; }
    public string SourceText => _original;
    public string ResultText => _translation;
    public string ActionName => TaskPrompts.GetActionName(_settings.TaskPrompt);
    private string ResultLabel => TaskPrompts.GetResultLabel(_settings.TaskPrompt);
    public bool IsTranslating => _isTranslating;
    public event EventHandler? TranslationStateChanged;

    public TranslationWindow(AppSettings settings, MagpieTranslationService translator)
    {
        _translator = translator;
        _targetLanguage = App.Current.TargetLanguage;
        _settings = CloneSettings(settings);
        _settings.TargetLanguage = _targetLanguage.CurrentLanguage;
        _pendingRequest = Task.CompletedTask;
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
        TaskPresetComboBox.ItemsSource = TaskPrompts.Presets;
        ConversationItems.ItemsSource = _conversation;
        _initialized = true;
        UpdateTaskPresentation();
        ApplyCaptionColors();
        RoundCorners();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        _themeSettings.Changed += ThemeSettings_Changed;
        _targetLanguage.Changed += TargetLanguage_Changed;
        App.Current.Settings.Saved += Settings_Saved;
        App.Current.History.Changed += History_Changed;
        Closed += OnClosed;
    }

    private void History_Changed(object? sender, EventArgs e)
    {
        if (_historyEntryId is null || IsClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(UpdateFavoriteButton);
    }

    public void PrepareForSelection(IntPtr sourceWindow, AppSettings settings)
    {
        if (IsClosed)
        {
            return;
        }

        BeginNewSession();
        CopySettings(settings);
        _settings.TargetLanguage = _targetLanguage.CurrentLanguage;

        _conversation.Clear();
        ConversationHeading.Visibility = Visibility.Collapsed;
        _original = string.Empty;
        _translation = string.Empty;
        LastError = null;
        _isTranslating = true;
        _isAnswering = false;
        _originalExpanded = false;
        OriginalTextBlock.MaxLines = 3;
        OriginalTextBlock.Text = "正在读取选中文字…";
        ExpandOriginalButton.Content = "展开";
        ExpandOriginalButton.Visibility = Visibility.Collapsed;
        CharacterCountTextBlock.Text = string.Empty;
        TranslationTextBlock.Text = string.Empty;
        TranslationTextBlock.Visibility = Visibility.Collapsed;
        TranslationErrorBar.IsOpen = false;
        CancelTranslationButton.Visibility = Visibility.Collapsed;
        CancelTranslationButton.IsEnabled = true;
        RetryTranslationButton.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        _historyEntryId = null;
        UpdateFavoriteButton();
        LoadingTextBlock.Text = "正在读取选中文字…";
        LoadingHintTextBlock.Text = $"正在从当前应用读取选区，随后会通过 Magpie {ActionName}。";
        LoadingPanel.Visibility = Visibility.Visible;
        TranslationProgressRing.IsActive = true;
        QuestionTextBox.Text = string.Empty;
        QuestionTextBox.IsEnabled = false;
        AskButton.IsEnabled = false;
        ComposerStatusTextBlock.Text = "读取选区中";
        LatestButton.Visibility = Visibility.Collapsed;

        TargetLanguageComboBox.SelectedItem = _targetLanguage.CurrentLanguage;
        UpdateTaskPresentation();
        BodyScroller.ChangeView(null, 0, null, true);
        PlaceAtTopRight(sourceWindow);
    }

    public void HideForReuse()
    {
        if (IsClosed)
        {
            return;
        }

        BeginNewSession();
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

    private void BeginNewSession()
    {
        _languageRevision++;
        _translationTask = null;
        _pendingRequest = Task.CompletedTask;
        _isAnswering = false;
        var previousCancellation = _languageCancellation;
        _languageCancellation = new CancellationTokenSource();
        previousCancellation.Cancel();
        previousCancellation.Dispose();
    }

    private static AppSettings CloneSettings(AppSettings settings) => new()
    {
        MagpieBaseUrl = settings.MagpieBaseUrl,
        ModelId = settings.ModelId,
        TargetLanguage = settings.TargetLanguage,
        TaskPrompt = settings.TaskPrompt,
        HotkeyModifiers = settings.HotkeyModifiers,
        HotkeyVirtualKey = settings.HotkeyVirtualKey,
        StartWithWindows = settings.StartWithWindows,
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
        EnableStreaming = settings.EnableStreaming,
        EnableTranslationCache = settings.EnableTranslationCache,
        EnableTranslationHistory = settings.EnableTranslationHistory,
        HistoryLimit = settings.HistoryLimit,
        ReasoningEffortByModel = CloneReasoningEfforts(settings.ReasoningEffortByModel)
    };

    private void CopySettings(AppSettings settings)
    {
        _settings.MagpieBaseUrl = settings.MagpieBaseUrl;
        _settings.ModelId = settings.ModelId;
        _settings.TaskPrompt = settings.TaskPrompt;
        _settings.HotkeyModifiers = settings.HotkeyModifiers;
        _settings.HotkeyVirtualKey = settings.HotkeyVirtualKey;
        _settings.StartWithWindows = settings.StartWithWindows;
        _settings.MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _settings.EnableStreaming = settings.EnableStreaming;
        _settings.EnableTranslationCache = settings.EnableTranslationCache;
        _settings.EnableTranslationHistory = settings.EnableTranslationHistory;
        _settings.HistoryLimit = settings.HistoryLimit;
        _settings.ReasoningEffortByModel = CloneReasoningEfforts(settings.ReasoningEffortByModel);
    }

    private static string GetReasoningEffort(AppSettings settings, string modelId) =>
        settings.ReasoningEffortByModel is { } efforts && efforts.TryGetValue(modelId, out var effort)
            ? effort
            : string.Empty;

    private static Dictionary<string, string> CloneReasoningEfforts(Dictionary<string, string>? values) =>
        values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    private void UpdateTaskPresentation()
    {
        var title = ActionName == "处理" ? "划词助手" : $"划词{ActionName}";
        Title = $"{title} - QuickTranslate";
        PopupTitleBar.Title = title;
        SyncTaskPreset();
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
        var previousReasoning = GetReasoningEffort(_settings, _settings.ModelId);
        var updatedReasoning = GetReasoningEffort(updated, updated.ModelId);
        var requiresReprocess = updated.TaskPrompt != _settings.TaskPrompt ||
            updated.ModelId != _settings.ModelId ||
            updated.MagpieBaseUrl != _settings.MagpieBaseUrl ||
            updatedReasoning != previousReasoning;
        CopySettings(updated);
        UpdateTaskPresentation();
        if (requiresReprocess)
        {
            RestartProcessing("任务、模型或思考强度已更新，请在新结果显示后重新发送。");
        }
    }

    private void SyncTaskPreset()
    {
        var current = TaskPrompts.Normalize(_settings.TaskPrompt);
        var preset = TaskPrompts.Presets.FirstOrDefault(item =>
                         item.Prompt is { } prompt && string.Equals(prompt, current, StringComparison.Ordinal))
                     ?? TaskPrompts.Presets[^1];

        _syncingPreset = true;
        try
        {
            TaskPresetComboBox.SelectedItem = preset;
        }
        finally
        {
            _syncingPreset = false;
        }

        ToolTipService.SetToolTip(TaskPresetComboBox, preset.Prompt is null
            ? "当前使用设置页里的自定义提示词，要修改请到设置"
            : $"{preset.Label}：与设置页同步，切换后按新任务重新处理原文");
    }

    private void TaskPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingPreset || TaskPresetComboBox.SelectedItem is not TaskPreset preset)
        {
            return;
        }

        if (preset.Prompt is not { } prompt)
        {
            // "自定义" only mirrors the prompt defined in settings, it cannot be chosen here.
            SyncTaskPreset();
            return;
        }

        if (string.Equals(prompt, TaskPrompts.Normalize(_settings.TaskPrompt), StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var updated = App.Current.Settings.Load();
            updated.TaskPrompt = prompt;
            App.Current.Settings.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SyncTaskPreset();
            TranslationErrorBar.Title = "未能保存任务设置";
            TranslationErrorBar.Message = ex.Message;
            TranslationErrorBar.IsOpen = true;
        }
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

    private AppSettings SnapshotSettings() => CloneSettings(_settings);

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
        _historyEntryId = null;
        UpdateFavoriteButton();
        LastError = null;
        _isTranslating = true;
        UpdateComposerState();
        TranslationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowCaptureError(string error, bool showElevationAction = false)
    {
        if (IsClosed)
        {
            return;
        }

        OriginalTextBlock.Text = "没有读取到选中文字";
        ShowTranslationError("未能读取选区", error);
        CaptureErrorActionButton.Visibility =
            showElevationAction && !ElevationService.IsCurrentProcessElevated()
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void CaptureErrorActionButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureErrorActionButton.IsEnabled = false;
        var restart = await App.Current.RequestElevatedRestartAsync();
        if (restart.Started || IsClosed)
        {
            return;
        }

        TranslationErrorBar.Message =
            $"{TranslationErrorBar.Message}\n{restart.Error}";
        CaptureErrorActionButton.IsEnabled = true;
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
        CancelTranslationButton.IsEnabled = true;
        CancelTranslationButton.Visibility = Visibility.Visible;
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
        _activeRequestCancellation = requestCancellation;
        var builder = new StringBuilder();
        var lastPreviewAt = 0L;
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
            if (settings.EnableStreaming)
            {
                await foreach (var chunk in _translator.StreamTranslateAsync(original, settings, token)
                                   .WithCancellation(token))
                {
                    token.ThrowIfCancellationRequested();
                    builder.Append(chunk);
                    var now = Environment.TickCount64;
                    if (now - lastPreviewAt >= 80)
                    {
                        UpdateStreamingPreview(builder.ToString());
                        lastPreviewAt = now;
                    }
                }
            }
            else
            {
                builder.Append(await _translator.TranslateAsync(original, settings, token));
            }

            token.ThrowIfCancellationRequested();
            var result = builder.ToString().Trim();
            if (result.Length == 0)
            {
                throw new InvalidOperationException("Magpie 返回了空结果。");
            }

            _translation = result;
            TranslationTextBlock.Text = result;
            TranslationTextBlock.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Collapsed;
            TranslationProgressRing.IsActive = false;
            CopyButton.IsEnabled = true;
            _historyEntryId = App.Current.History.FindEntryId(settings, original);
            UpdateFavoriteButton();
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
            _activeRequestCancellation = null;
            if (!IsClosed && revision == _languageRevision)
            {
                _isTranslating = false;
                LoadingPanel.Visibility = Visibility.Collapsed;
                TranslationProgressRing.IsActive = false;
                CancelTranslationButton.Visibility = Visibility.Collapsed;
                UpdateComposerState();
                TranslationStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void UpdateStreamingPreview(string text)
    {
        if (IsClosed || string.IsNullOrEmpty(text))
        {
            return;
        }

        TranslationTextBlock.Text = text;
        TranslationTextBlock.Visibility = Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Collapsed;
        TranslationProgressRing.IsActive = false;
    }

    private void ShowTranslationError(string title, string error)
    {
        LastError = error;
        _isTranslating = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        TranslationProgressRing.IsActive = false;
        CancelTranslationButton.Visibility = Visibility.Collapsed;
        TranslationErrorBar.Title = title;
        TranslationErrorBar.Message = error;
        TranslationErrorBar.IsOpen = true;
        CaptureErrorActionButton.Visibility = Visibility.Collapsed;
        _historyEntryId = null;
        UpdateFavoriteButton();
        RetryTranslationButton.Visibility = string.IsNullOrEmpty(_original) ? Visibility.Collapsed : Visibility.Visible;
        UpdateComposerState();
        TranslationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        CancelTranslationButton.IsEnabled = false;
        _activeRequestCancellation?.Cancel();
    }

    private async void RetryTranslationButton_Click(object sender, RoutedEventArgs e) => await TranslateAsync(_original);

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyEntryId is not { } id)
        {
            return;
        }

        var entry = App.Current.History.GetEntry(id);
        if (entry is null)
        {
            _historyEntryId = null;
            UpdateFavoriteButton();
            return;
        }

        App.Current.History.SetFavorite(id, !entry.IsFavorite);
        UpdateFavoriteButton();
    }

    private void UpdateFavoriteButton()
    {
        if (_historyEntryId is not { } id || App.Current.History.GetEntry(id) is not { } entry)
        {
            FavoriteButton.Visibility = Visibility.Collapsed;
            return;
        }

        FavoriteButton.Icon = new FontIcon { Glyph = entry.IsFavorite ? "\uE735" : "\uE734", FontSize = 16 };
        FavoriteButton.Label = entry.IsFavorite ? "已收藏" : "收藏";
        ToolTipService.SetToolTip(FavoriteButton, entry.IsFavorite ? "取消收藏" : "收藏这条记录");
        FavoriteButton.Visibility = Visibility.Visible;
    }

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
        _activeRequestCancellation = requestCancellation;
        _isAnswering = true;
        turn.Start();
        CancelTranslationButton.IsEnabled = true;
        CancelTranslationButton.Visibility = Visibility.Visible;
        UpdateComposerState();
        try
        {
            var history = string.Join("\n\n", _conversation.TakeWhile(entry => entry != turn)
                .Where(entry => !entry.IsPending && !string.IsNullOrEmpty(entry.Answer))
                .Select(entry => $"你：{entry.Question}\n\n助手：{entry.Answer}"));
            var settings = SnapshotSettings();
            var builder = new StringBuilder();
            var lastPreviewAt = 0L;
            if (settings.EnableStreaming)
            {
                await foreach (var chunk in _translator.StreamAskAsync(
                                   _original, _translation, history, turn.Question, settings, token)
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
                builder.Append(await _translator.AskAsync(
                    _original, _translation, history, turn.Question, settings, token));
            }

            token.ThrowIfCancellationRequested();
            var answer = builder.ToString().Trim();
            if (answer.Length == 0)
            {
                throw new InvalidOperationException("Magpie 返回了空回答。");
            }

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
            _activeRequestCancellation = null;
            if (!IsClosed && revision == _languageRevision)
            {
                _isAnswering = false;
                CancelTranslationButton.Visibility = Visibility.Collapsed;
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
            HideForReuse();
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || IsClosed)
        {
            return;
        }

        // The title bar X only dismisses the current result. Reusing the same
        // window keeps the next shortcut response fast and avoids churn.
        args.Cancel = true;
        HideForReuse();
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
        var margin = (int)Math.Round(14 * scale);
        var width = Math.Max(1, Math.Min((int)Math.Round(400 * scale), workArea.Width - margin * 2));
        var height = Math.Max(1, Math.Min((int)Math.Round(520 * scale), workArea.Height - margin * 2));
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
        AppWindow.Closing -= AppWindow_Closing;
        _themeSettings.Changed -= ThemeSettings_Changed;
        _targetLanguage.Changed -= TargetLanguage_Changed;
        App.Current.Settings.Saved -= Settings_Saved;
        App.Current.History.Changed -= History_Changed;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _languageCancellation.Cancel();
        _languageCancellation.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}

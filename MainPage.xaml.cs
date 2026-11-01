using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate;

public sealed partial class MainPage : Page
{
    private readonly AppSettings _settings;
    private readonly TargetLanguageService _targetLanguage;
    private CancellationTokenSource? _manualTranslationCancellation;
    private bool _isLoaded;
    private bool _isRefreshingModels;

    public MainPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        _settings = App.Current.Settings.Load();
        _targetLanguage = App.Current.TargetLanguage;
        BaseUrlBox.Text = _settings.MagpieBaseUrl;
        TargetLanguageBox.ItemsSource = _targetLanguage.Languages;
        SyncTargetLanguage();
        UpdateTaskPresentation();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _targetLanguage.Changed += TargetLanguage_Changed;
        App.Current.Settings.Saved += Settings_Saved;
        SyncTargetLanguage();
        SyncWorkflowSettings();
        if (ModelComboBox.ItemsSource is null)
        {
            await RefreshModelsAsync(_settings.ModelId);
        }
        if (_isLoaded && !App.Current.GlobalHotkey.IsRegistered)
        {
            SetStatus($"快捷键 {App.Current.GlobalHotkey.CurrentHotkey.DisplayText} 注册失败，请到设置页面选择其他组合。", isError: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _targetLanguage.Changed -= TargetLanguage_Changed;
        App.Current.Settings.Saved -= Settings_Saved;
        _manualTranslationCancellation?.Cancel();
    }

    private void Settings_Saved(object? sender, EventArgs e)
    {
        var previousPrompt = _settings.TaskPrompt;
        SyncWorkflowSettings();
        if (previousPrompt != _settings.TaskPrompt)
        {
            _manualTranslationCancellation?.Cancel();
            SetStatus("提示词已更新，点击任务按钮即可按新要求处理原文。");
        }
    }

    private void SyncWorkflowSettings()
    {
        var saved = App.Current.Settings.Load();
        if (_settings.TaskPrompt != saved.TaskPrompt)
        {
            ResultTextBox.Text = string.Empty;
        }
        _settings.TaskPrompt = saved.TaskPrompt;
        _settings.HotkeyModifiers = saved.HotkeyModifiers;
        _settings.HotkeyVirtualKey = saved.HotkeyVirtualKey;
        UpdateTaskPresentation();
    }

    private void UpdateTaskPresentation()
    {
        var action = TaskPrompts.GetActionName(_settings.TaskPrompt);
        ShortcutHintTextBlock.Text = $"选中文字，按 {HotkeyGesture.FromSettings(_settings).DisplayText}，通过 Magpie {action}并在右上角显示结果。";
        ManualTaskTitleTextBlock.Text = $"手动{action}";
        TaskHintTextBlock.Text = "使用设置页面中的提示词；结果留在当前页面，不会额外弹窗。";
        TranslateButton.Content = action;
        ResultTextBox.Header = TaskPrompts.GetResultLabel(_settings.TaskPrompt);
    }

    private void SyncTargetLanguage()
    {
        _settings.TargetLanguage = _targetLanguage.CurrentLanguage;
        TargetLanguageBox.SelectedItem = _targetLanguage.CurrentLanguage;
    }

    private void TargetLanguage_Changed(object? sender, EventArgs e)
    {
        SyncTargetLanguage();
        _manualTranslationCancellation?.Cancel();
        SaveStatusText.Text = "目标语言已同步并保存";
        SetStatus($"目标语言已切换为{_targetLanguage.CurrentLanguage}。");
    }

    private void TargetLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_targetLanguage is null || TargetLanguageBox.SelectedItem is not string language || language == _targetLanguage.CurrentLanguage)
        {
            return;
        }

        try
        {
            _targetLanguage.SetLanguage(language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SyncTargetLanguage();
            SetStatus($"目标语言未能保存：{ex.Message}", isError: true);
        }
    }

    public void SetSourceText(string text) => SourceTextBox.Text = text;

    public string SourceText => SourceTextBox.Text.Trim();

    public void SetResultText(string text) => ResultTextBox.Text = text;

    public void SetStatus(string message, bool isError = false)
    {
        StatusBar.Title = isError ? "处理失败" : "状态";
        StatusBar.Message = message;
        StatusBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        StatusBar.IsOpen = true;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Settings.Load();
        settings.MagpieBaseUrl = BaseUrlBox.Text.Trim();
        settings.ModelId = ModelComboBox.SelectedValue as string ?? string.Empty;
        settings.TargetLanguage = _targetLanguage.CurrentLanguage;

        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            SetStatus("请先从模型列表中选择模型。", isError: true);
            return;
        }

        try
        {
            App.Current.Settings.Save(settings);
            _settings.MagpieBaseUrl = settings.MagpieBaseUrl;
            _settings.ModelId = settings.ModelId;
            SaveStatusText.Text = "已保存";
            SetStatus("Magpie 设置已保存。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"设置未能保存：{ex.Message}", isError: true);
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(ModelComboBox.SelectedValue as string ?? _settings.ModelId);

    private async Task RefreshModelsAsync(string? preferredModel)
    {
        if (_isRefreshingModels)
        {
            return;
        }
        _isRefreshingModels = true;
        RefreshModelsButton.IsEnabled = false;

        try
        {
            var models = await App.Current.Translator.GetModelsAsync(BaseUrlBox.Text, CancellationToken.None);
            ModelComboBox.ItemsSource = models;
            var selected = models.FirstOrDefault(model => model.Id.Equals(preferredModel, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(model => model.Id.Equals("auto/gemini-3.8-flash", StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault();

            ModelComboBox.SelectedItem = selected;
            if (models.Count == 0)
            {
                SetStatus("Magpie 未返回可用模型。", isError: true);
            }
            else
            {
                SetStatus($"已加载 {models.Count} 个 Magpie 模型。");
            }
        }
        catch (Exception ex)
        {
            ModelComboBox.ItemsSource = Array.Empty<MagpieModel>();
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            _isRefreshingModels = false;
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private async void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        var sourceText = SourceTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            SetStatus("请输入要处理的文字。", isError: true);
            return;
        }

        TranslateButton.IsEnabled = false;
        SetStatus("正在请求 Magpie…");
        using var requestCancellation = new CancellationTokenSource();
        _manualTranslationCancellation = requestCancellation;
        var targetLanguage = _targetLanguage.CurrentLanguage;
        var settings = App.Current.Settings.Load();
        settings.MagpieBaseUrl = BaseUrlBox.Text;
        settings.ModelId = ModelComboBox.SelectedValue as string ?? string.Empty;
        settings.TargetLanguage = targetLanguage;
        var taskPrompt = settings.TaskPrompt;
        var action = TaskPrompts.GetActionName(taskPrompt);
        try
        {
            var translation = await App.Current.Translator.TranslateAsync(sourceText, settings, requestCancellation.Token);
            requestCancellation.Token.ThrowIfCancellationRequested();
            ResultTextBox.Text = translation;
            SetStatus($"{action}完成。");
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            if (_isLoaded && targetLanguage == _targetLanguage.CurrentLanguage && taskPrompt == _settings.TaskPrompt)
            {
                SetStatus($"{action}已取消。");
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            _manualTranslationCancellation = null;
            if (_isLoaded)
            {
                TranslateButton.IsEnabled = true;
            }
        }
    }
}

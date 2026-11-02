using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate;

public sealed partial class SettingsPage : Page
{
    private bool _initialized;
    private bool _syncingPreset;
    private bool _isSaving;

    public SettingsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        var settings = App.Current.Settings.Load();
        HotkeyKeyComboBox.ItemsSource = HotkeyGesture.Keys;
        ApplyHotkey(HotkeyGesture.FromSettings(settings));
        TaskPromptBox.Text = TaskPrompts.Normalize(settings.TaskPrompt);
        StartWithWindowsToggle.IsOn = settings.StartWithWindows;
        AdministratorModeToggle.IsOn = settings.UseAdministratorMode;
        AutoElevateToggle.IsOn = settings.AutoElevateForProtectedApps;
        MinimizeToTrayToggle.IsOn = settings.MinimizeToTrayOnClose;
        StreamingToggle.IsOn = settings.EnableStreaming;
        TranslationCacheToggle.IsOn = settings.EnableTranslationCache;
        TranslationHistoryToggle.IsOn = settings.EnableTranslationHistory;
        SelectHistoryLimit(settings.HistoryLimit);
        RefreshStartupState(syncToggle: true);
        RefreshElevationState();
        _initialized = true;
        SyncPromptPreset();
        UpdateHotkeyPreview();
        Loaded += (_, _) =>
        {
            UpdateActiveHotkey();
            RefreshStartupState();
            RefreshElevationState();
            RefreshAuxiliaryState();
        };
    }

    private HotkeyGesture SelectedHotkey => new(
        (ControlCheckBox.IsChecked == true ? HotkeyGesture.Control : 0) |
        (AltCheckBox.IsChecked == true ? HotkeyGesture.Alt : 0) |
        (ShiftCheckBox.IsChecked == true ? HotkeyGesture.Shift : 0) |
        (WindowsCheckBox.IsChecked == true ? HotkeyGesture.Windows : 0),
        HotkeyKeyComboBox.SelectedItem is HotkeyKeyOption key ? key.Value : 0);

    private void ApplyHotkey(HotkeyGesture hotkey)
    {
        ControlCheckBox.IsChecked = (hotkey.Modifiers & HotkeyGesture.Control) != 0;
        AltCheckBox.IsChecked = (hotkey.Modifiers & HotkeyGesture.Alt) != 0;
        ShiftCheckBox.IsChecked = (hotkey.Modifiers & HotkeyGesture.Shift) != 0;
        WindowsCheckBox.IsChecked = (hotkey.Modifiers & HotkeyGesture.Windows) != 0;
        HotkeyKeyComboBox.SelectedItem = HotkeyGesture.Keys.First(key => key.Value == hotkey.VirtualKey);
    }

    private void HotkeyChoice_Changed(object sender, RoutedEventArgs e) => UpdateHotkeyPreview();

    private void HotkeyKeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHotkeyPreview();

    private void UpdateHotkeyPreview()
    {
        if (_initialized)
        {
            var hotkey = SelectedHotkey;
            HotkeyPreviewTextBlock.Text = hotkey.ValidationError ?? $"保存后使用：{hotkey.DisplayText}";
        }
    }

    private void UpdateActiveHotkey()
    {
        var service = App.Current.GlobalHotkey;
        ActiveHotkeyTextBlock.Text = service.IsRegistered
            ? $"当前生效：{service.CurrentHotkey.DisplayText}"
            : "当前快捷键注册失败，请选择其他组合后保存。";
    }

    private void RefreshStartupState(bool syncToggle = false)
    {
        try
        {
            var settings = App.Current.Settings.Load();
            var elevatedMode = settings.UseAdministratorMode;
            var status = App.Current.Startup.GetStatus(elevatedMode);
            if (syncToggle)
            {
                StartWithWindowsToggle.IsOn = settings.StartWithWindows;
            }

            StartupStatusTextBlock.Text = elevatedMode
                ? status.State switch
                {
                    StartupRegistrationState.CurrentExecutable =>
                        "已注册管理员模式启动任务，登录或解锁时会直接以后台管理员权限启动，不弹 UAC。"
                        + (App.Current.Startup.IsElevatedStartupTaskRegistered
                            ? " 该任务同时覆盖睡眠或休眠唤醒后的解锁。"
                            : string.Empty),
                    StartupRegistrationState.StalePath =>
                        $"检测到旧的管理员模式启动任务，路径与当前程序不一致：{status.RegisteredExecutablePath ?? "无法识别"}。保存设置可自动修复。",
                    _ => "管理员模式启动任务尚未注册。"
                         + (App.Current.Startup.LastElevatedTaskError is { Length: > 0 } error
                             ? $" {error}"
                             : " 保存设置，或先以管理员权限重启后会自动创建。")
                }
                : status.State switch
                {
                    StartupRegistrationState.CurrentExecutable =>
                        "已注册当前用户的启动项，并指向当前程序。Windows 也可以在任务管理器的“启动应用”中禁用它。"
                        + UnlockTaskHint(),
                    StartupRegistrationState.StalePath =>
                        $"检测到旧的启动项，路径与当前程序不一致：{status.RegisteredExecutablePath ?? "无法识别"}。开启“开机启动”并保存可自动修复。",
                    _ => "尚未注册开机启动项，开启后点击“保存并应用”生效。"
                };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartupStatusTextBlock.Text = $"无法读取系统启动项：{ex.Message}";
        }
    }

    private void RefreshElevationState()
    {
        var elevated = ElevationService.IsCurrentProcessElevated();
        ElevationStatusTextBlock.Text = elevated
            ? "当前权限：管理员。可以读取管理员窗口中的选区；普通窗口仍然可用。"
            : "当前权限：普通用户。管理员窗口需要切换到管理员模式，普通窗口不受影响。";
        if (!ElevationService.IsExecutableInProtectedLocation())
        {
            ElevationStatusTextBlock.Text +=
                " 当前程序位于用户可写目录，出于安全考虑不会创建管理员模式开机启动任务；需要时会请求 UAC。";
        }

        RestartElevatedButton.IsEnabled = !elevated && !_isSaving;
        RestartElevatedButton.Content = elevated ? "已以管理员权限运行" : "以管理员权限重启";
    }

    private string UnlockTaskHint()
    {
        if (App.Current.Startup.IsUnlockTaskRegistered)
        {
            return " 睡眠或休眠唤醒后，解锁时会自动补启动后台进程。";
        }

        return App.Current.Startup.LastUnlockTaskError is { Length: > 0 } error
            ? $" 但未能注册“解锁后自动启动”：{error}"
            : string.Empty;
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e) => ApplyHotkey(HotkeyGesture.Default);

    private void PromptPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncingPreset || PromptPresetComboBox.SelectedItem is not ComboBoxItem preset)
        {
            return;
        }

        var prompt = (preset.Tag as string) switch
        {
            "translation" => TaskPrompts.Translation,
            "technical" => TaskPrompts.Technical,
            "explanation" => TaskPrompts.Explanation,
            "summary" => TaskPrompts.Summary,
            _ => null
        };
        if (prompt is not null)
        {
            TaskPromptBox.Text = prompt;
        }
    }

    private void TaskPromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            SyncPromptPreset();
        }
    }

    private void SyncPromptPreset()
    {
        var id = TaskPromptBox.Text.Trim() switch
        {
            TaskPrompts.Translation => "translation",
            TaskPrompts.Technical => "technical",
            TaskPrompts.Explanation => "explanation",
            TaskPrompts.Summary => "summary",
            _ => "custom"
        };
        _syncingPreset = true;
        PromptPresetComboBox.SelectedItem = PromptPresetComboBox.Items.OfType<ComboBoxItem>()
            .First(item => item.Tag as string == id);
        _syncingPreset = false;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        var hotkey = SelectedHotkey;
        if (hotkey.ValidationError is { } error)
        {
            ShowStatus("快捷键无效", error, isError: true);
            return;
        }
        var prompt = TaskPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ShowStatus("提示词不能为空", "可以先选择一个任务预设，再修改提示词。", isError: true);
            return;
        }

        var service = App.Current.GlobalHotkey;
        var startup = App.Current.Startup;
        var previousSettings = App.Current.Settings.Load();
        var previousHotkey = service.CurrentHotkey;
        var hotkeyApplied = false;
        var startupAttempted = false;
        var startWithWindows = StartWithWindowsToggle.IsOn;
        var useAdministratorMode = AdministratorModeToggle.IsOn;
        var autoElevate = AutoElevateToggle.IsOn;
        var elevated = ElevationService.IsCurrentProcessElevated();
        var restartRequired = useAdministratorMode && !elevated;
        var minimizeToTray = MinimizeToTrayToggle.IsOn;
        var enableStreaming = StreamingToggle.IsOn;
        var enableCache = TranslationCacheToggle.IsOn;
        var enableHistory = TranslationHistoryToggle.IsOn;
        var historyLimit = SelectedHistoryLimit;
        _isSaving = true;
        SetFormEnabled(false);
        try
        {
            await service.ChangeAsync(hotkey);
            hotkeyApplied = true;
            if (!restartRequired)
            {
                startupAttempted = true;
                startup.SetEnabled(startWithWindows, useAdministratorMode);
            }

            // Reload to preserve language/model preferences changed by another window.
            var settings = App.Current.Settings.Load();
            settings.HotkeyModifiers = hotkey.Modifiers;
            settings.HotkeyVirtualKey = hotkey.VirtualKey;
            settings.TaskPrompt = prompt;
            settings.StartWithWindows = startWithWindows;
            settings.UseAdministratorMode = useAdministratorMode;
            settings.AutoElevateForProtectedApps = autoElevate;
            settings.MinimizeToTrayOnClose = minimizeToTray;
            settings.EnableStreaming = enableStreaming;
            settings.EnableTranslationCache = enableCache;
            settings.EnableTranslationHistory = enableHistory;
            settings.HistoryLimit = historyLimit;
            App.Current.Settings.Save(settings);

            if (restartRequired)
            {
                ShowStatus("正在请求管理员权限", "设置已保存。请在 UAC 窗口中确认，QuickTranslate 会自动重启到管理员模式。");
                var restart = await App.Current.RequestElevatedRestartAsync();
                if (restart.Started)
                {
                    return;
                }

                var reverted = App.Current.Settings.Load();
                reverted.UseAdministratorMode = false;
                reverted.AutoElevateForProtectedApps = autoElevate;
                App.Current.Settings.Save(reverted);
                AdministratorModeToggle.IsOn = false;

                try
                {
                    startup.SetEnabled(startWithWindows, elevated: false);
                }
                catch (Exception startupError) when (startupError is IOException or UnauthorizedAccessException
                                                       or InvalidOperationException)
                {
                    ShowStatus("已取消管理员模式",
                        $"{restart.Error}\n其他设置已保存，但启动项未能应用：{startupError.Message}", isError: true);
                    return;
                }

                ShowStatus("已取消管理员模式",
                    $"{restart.Error}\n其他设置已保存，QuickTranslate 仍以普通权限运行。", isError: true);
                return;
            }

            var closeBehavior = minimizeToTray ? "关闭主窗口时隐藏到托盘" : "关闭主窗口时退出程序";
            var mode = useAdministratorMode
                ? "管理员模式已开启"
                : elevated
                    ? "管理员模式已关闭，但当前窗口仍是管理员权限；下次启动恢复普通权限"
                    : "普通权限模式";
            ShowStatus("设置已保存",
                $"快捷键：{hotkey.DisplayText}；{closeBehavior}；{mode}。启动、提示词与输出设置已应用。");
        }
        catch (Exception ex)
        {
            var rollbackErrors = new List<string>();
            if (startupAttempted)
            {
                try
                {
                    startup.SetEnabled(previousSettings.StartWithWindows, previousSettings.UseAdministratorMode);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add($"启动项未能恢复：{rollbackError.Message}");
                }
            }
            if (hotkeyApplied)
            {
                try
                {
                    await service.ChangeAsync(previousHotkey);
                }
                catch (Exception)
                {
                    rollbackErrors.Add($"当前快捷键为 {service.CurrentHotkey.DisplayText}，请重新保存设置。");
                }
            }
            var recovery = rollbackErrors.Count == 0
                ? "快捷键和启动项保持原设置。"
                : string.Join("\n", rollbackErrors);
            ShowStatus("设置未能应用", $"{ex.Message}\n{recovery}", isError: true);
        }
        finally
        {
            _isSaving = false;
            SetFormEnabled(true);
            UpdateActiveHotkey();
            RefreshStartupState(syncToggle: true);
            RefreshElevationState();
            RefreshAuxiliaryState();
        }
    }

    private async void RestartElevatedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSaving || ElevationService.IsCurrentProcessElevated())
        {
            return;
        }

        _isSaving = true;
        SetFormEnabled(false);
        RefreshElevationState();
        var changed = false;
        try
        {
            var settings = App.Current.Settings.Load();
            settings.UseAdministratorMode = true;
            settings.AutoElevateForProtectedApps = AutoElevateToggle.IsOn;
            App.Current.Settings.Save(settings);
            changed = true;
            AdministratorModeToggle.IsOn = true;
            ShowStatus("正在请求管理员权限", "请在 UAC 窗口中确认，QuickTranslate 会自动重启。");

            var restart = await App.Current.RequestElevatedRestartAsync();
            if (restart.Started)
            {
                return;
            }

            var reverted = App.Current.Settings.Load();
            reverted.UseAdministratorMode = false;
            App.Current.Settings.Save(reverted);
            AdministratorModeToggle.IsOn = false;
            ShowStatus("未切换到管理员模式", restart.Error ?? "管理员权限请求失败。", isError: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (changed)
            {
                try
                {
                    var reverted = App.Current.Settings.Load();
                    reverted.UseAdministratorMode = false;
                    App.Current.Settings.Save(reverted);
                    AdministratorModeToggle.IsOn = false;
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    ShowStatus("无法恢复设置", rollbackError.Message, isError: true);
                }
            }

            ShowStatus("未切换到管理员模式", ex.Message, isError: true);
        }
        finally
        {
            _isSaving = false;
            SetFormEnabled(true);
            RefreshElevationState();
            RefreshStartupState(syncToggle: true);
            RefreshAuxiliaryState();
        }
    }

    private void SetFormEnabled(bool isEnabled)
    {
        Control[] controls =
        [
            ControlCheckBox, AltCheckBox, ShiftCheckBox, WindowsCheckBox, HotkeyKeyComboBox,
            ResetHotkeyButton, PromptPresetComboBox, TaskPromptBox, StartWithWindowsToggle,
            AdministratorModeToggle, AutoElevateToggle, RestartElevatedButton,
            MinimizeToTrayToggle, StreamingToggle, TranslationCacheToggle, TranslationHistoryToggle,
            HistoryLimitComboBox, ClearCacheButton, ClearHistoryButton, SaveSettingsButton
        ];
        foreach (var control in controls)
        {
            control.IsEnabled = isEnabled;
        }
    }

    private void ShowStatus(string title, string message, bool isError = false)
    {
        SettingsStatusBar.Title = title;
        SettingsStatusBar.Message = message;
        SettingsStatusBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        SettingsStatusBar.IsOpen = true;
    }

    private int SelectedHistoryLimit =>
        HistoryLimitComboBox.SelectedItem is ComboBoxItem item &&
        int.TryParse(item.Tag as string, out var value)
            ? value
            : 200;

    private void SelectHistoryLimit(int value)
    {
        var normalized = Math.Clamp(value, 20, 1000);
        HistoryLimitComboBox.SelectedItem = HistoryLimitComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == normalized.ToString())
            ?? HistoryLimitComboBox.Items.OfType<ComboBoxItem>()
                .First(item => item.Tag as string == "200");
    }

    private void RefreshAuxiliaryState()
    {
        CacheStatusTextBlock.Text =
            $"当前有 {App.Current.Cache.Count} 条可用缓存；相同原文、模型、语言和提示词会直接复用。";
        HistoryStatusTextBlock.Text = $"当前保存 {App.Current.History.Count} 条历史记录。";
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Current.Cache.Clear();
            RefreshAuxiliaryState();
            ShowStatus("缓存已清空", "下一次处理文字时会重新请求 Magpie。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法清空缓存", ex.Message, isError: true);
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空全部历史？",
            Content = "收藏记录也会一并删除，此操作无法撤销。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            App.Current.History.Clear();
            RefreshAuxiliaryState();
            ShowStatus("历史记录已清空", "后续处理仍会按当前开关继续记录。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法清空历史", ex.Message, isError: true);
        }
    }
}

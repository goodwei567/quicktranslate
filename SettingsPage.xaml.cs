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
        MinimizeToTrayToggle.IsOn = settings.MinimizeToTrayOnClose;
        RefreshStartupState(syncToggle: true);
        _initialized = true;
        SyncPromptPreset();
        UpdateHotkeyPreview();
        Loaded += (_, _) =>
        {
            UpdateActiveHotkey();
            RefreshStartupState();
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
            var registered = App.Current.Startup.IsEnabled;
            if (syncToggle)
            {
                StartWithWindowsToggle.IsOn = registered;
            }
            StartupStatusTextBlock.Text = registered
                ? "已注册当前用户的启动项。Windows 也可以在任务管理器的“启动应用”中禁用它。"
                : "尚未注册开机启动项，开启后点击“保存并应用”生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartupStatusTextBlock.Text = $"无法读取系统启动项：{ex.Message}";
        }
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
        var previousHotkey = service.CurrentHotkey;
        StartupRegistration? previousStartup = null;
        var hotkeyApplied = false;
        var startupAttempted = false;
        var startWithWindows = StartWithWindowsToggle.IsOn;
        var minimizeToTray = MinimizeToTrayToggle.IsOn;
        _isSaving = true;
        SetFormEnabled(false);
        try
        {
            previousStartup = startup.CaptureRegistration();
            await service.ChangeAsync(hotkey);
            hotkeyApplied = true;
            startupAttempted = true;
            startup.SetEnabled(startWithWindows);
            // Reload to preserve language/model preferences changed by another window.
            var settings = App.Current.Settings.Load();
            settings.HotkeyModifiers = hotkey.Modifiers;
            settings.HotkeyVirtualKey = hotkey.VirtualKey;
            settings.TaskPrompt = prompt;
            settings.StartWithWindows = startWithWindows;
            settings.MinimizeToTrayOnClose = minimizeToTray;
            App.Current.Settings.Save(settings);

            var closeBehavior = minimizeToTray ? "关闭主窗口时隐藏到托盘" : "关闭主窗口时退出程序";
            ShowStatus("设置已保存", $"快捷键：{hotkey.DisplayText}；{closeBehavior}。启动选项和提示词已应用。");
        }
        catch (Exception ex)
        {
            var rollbackErrors = new List<string>();
            if (startupAttempted && previousStartup is not null)
            {
                try
                {
                    startup.RestoreRegistration(previousStartup);
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
        }
    }

    private void SetFormEnabled(bool isEnabled)
    {
        Control[] controls =
        [
            ControlCheckBox, AltCheckBox, ShiftCheckBox, WindowsCheckBox, HotkeyKeyComboBox,
            ResetHotkeyButton, PromptPresetComboBox, TaskPromptBox, StartWithWindowsToggle,
            MinimizeToTrayToggle, SaveSettingsButton
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
}

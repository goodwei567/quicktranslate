using Microsoft.Win32;

namespace QuickTranslate.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickTranslate";

    public StartupRegistration CaptureRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new StartupRegistration(value, value is null ? RegistryValueKind.String : key!.GetValueKind(ValueName));
    }

    public bool IsEnabled => CaptureRegistration().Value is string command && !string.IsNullOrWhiteSpace(command);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定应用路径，请通过 QuickTranslate.exe 启动后重试。");
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException("无法打开当前用户的开机启动设置。");
            key.SetValue(ValueName, $"\"{executablePath}\" --background", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public void RestoreRegistration(StartupRegistration registration)
    {
        if (registration.Value is null)
        {
            SetEnabled(false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("无法恢复原有的开机启动设置。");
        key.SetValue(ValueName, registration.Value, registration.Kind);
    }
}

public sealed record StartupRegistration(object? Value, RegistryValueKind Kind);

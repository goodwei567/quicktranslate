using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace QuickTranslate.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickTranslate";
    private const string UnlockTaskName = "QuickTranslateUnlock";
    private const string ElevatedTaskName = "QuickTranslateElevatedStartup";

    /// <summary>
    /// Last failure while registering the unlock task, if any. Startup still works through the Run key.
    /// </summary>
    public string? LastUnlockTaskError { get; private set; }

    public string? LastElevatedTaskError { get; private set; }

    public StartupRegistration CaptureRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new StartupRegistration(value, value is null ? RegistryValueKind.String : key!.GetValueKind(ValueName));
    }

    public bool IsEnabled => GetStatus().MatchesCurrentExecutable;

    public StartupStatus GetStatus(bool elevated = false)
    {
        if (elevated)
        {
            var elevatedCommand = GetElevatedTaskCommand();
            if (string.IsNullOrWhiteSpace(elevatedCommand))
            {
                return new StartupStatus(StartupRegistrationState.NotRegistered, null);
            }

            var elevatedPath = ExtractExecutablePath(elevatedCommand);
            var current = Environment.ProcessPath;
            var matches = elevatedPath is not null && current is not null &&
                string.Equals(NormalizePath(elevatedPath), NormalizePath(current), StringComparison.OrdinalIgnoreCase);
            return new StartupStatus(
                matches ? StartupRegistrationState.CurrentExecutable : StartupRegistrationState.StalePath,
                elevatedPath);
        }

        var registration = CaptureRegistration();
        if (registration.Value is not string command || string.IsNullOrWhiteSpace(command))
        {
            return new StartupStatus(StartupRegistrationState.NotRegistered, null);
        }

        var registeredPath = ExtractExecutablePath(command);
        var currentPath = Environment.ProcessPath;
        var matchesCurrent = registeredPath is not null && currentPath is not null &&
            string.Equals(NormalizePath(registeredPath), NormalizePath(currentPath), StringComparison.OrdinalIgnoreCase);
        return new StartupStatus(
            matchesCurrent ? StartupRegistrationState.CurrentExecutable : StartupRegistrationState.StalePath,
            registeredPath);
    }

    public void SetEnabled(bool enabled, bool elevated = false)
    {
        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定应用路径，请通过 QuickTranslate.exe 启动后重试。");
            if (elevated)
            {
                if (!ElevationService.IsExecutableInProtectedLocation())
                {
                    // A highest-privilege task that starts an executable from a
                    // user-writable folder would turn that folder into a local
                    // privilege-escalation vector. Fall back to a normal startup
                    // entry and use an explicit UAC request when elevation is needed.
                    var warning =
                        "程序位于用户可写目录，出于安全考虑不会创建管理员模式开机启动任务。" +
                        "登录后会在遇到管理员窗口时请求 UAC；如需免 UAC 开机启动，请将程序安装到 Program Files。";
                    try
                    {
                        RemoveElevatedStartupTask();
                    }
                    catch (InvalidOperationException ex)
                    {
                        warning += $" 清理旧任务失败：{ex.Message}";
                    }

                    LastElevatedTaskError = warning;
                    EnsureNormalRegistration(executablePath);
                    return;
                }

                EnsureElevatedStartupTask(executablePath);
                RemoveNormalRegistration();
                return;
            }

            RemoveElevatedStartupTask();
            EnsureNormalRegistration(executablePath);
        }
        else
        {
            RemoveNormalRegistration();
            RemoveElevatedStartupTask();
        }
    }

    public void EnsureConfigured(bool enabled, bool elevated)
    {
        if (!enabled)
        {
            return;
        }

        if (elevated && !ElevationService.IsCurrentProcessElevated())
        {
            return;
        }

        try
        {
            var status = GetStatus(elevated);
            if (status.MatchesCurrentExecutable)
            {
                if (elevated)
                {
                    RemoveNormalRegistration();
                }
                else
                {
                    RemoveElevatedStartupTask();
                }
                return;
            }

            SetEnabled(true, elevated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Security.SecurityException or InvalidOperationException)
        {
            if (elevated)
            {
                LastElevatedTaskError = ex.Message;
            }
            else
            {
                LastUnlockTaskError = ex.Message;
            }
        }
    }

    public bool IsUnlockTaskRegistered => GetUnlockTaskCommand() is not null;

    public bool IsElevatedStartupTaskRegistered => GetElevatedTaskCommand() is not null;

    private void RemoveNormalRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        RemoveUnlockTask();
    }

    private void EnsureNormalRegistration(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("无法打开当前用户的开机启动设置。");
        key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        EnsureUnlockTask(executablePath);
    }

    /// <summary>
    /// Sleep and hibernate resume do not run Run-key entries, so a per-user task restarts the tray app
    /// the next time the session is unlocked. Failures are recorded instead of thrown: the Run key
    /// still covers logon, and the settings page shows the warning.
    /// </summary>
    public void EnsureUnlockTask(string? executablePath = null)
    {
        LastUnlockTaskError = null;
        executablePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            LastUnlockTaskError = "无法确定当前程序路径，未能注册解锁后自动启动。";
            return;
        }

        if (string.Equals(GetUnlockTaskCommand(), executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var xmlPath = Path.Combine(Path.GetTempPath(), "QuickTranslate-unlock-task.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildUnlockTaskXml(executablePath), Encoding.Unicode);
            var (ok, output) = RunSchtasks($"/Create /TN {UnlockTaskName} /XML \"{xmlPath}\" /F");
            if (!ok)
            {
                LastUnlockTaskError = string.IsNullOrWhiteSpace(output) ? "任务计划程序拒绝了请求。" : output;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastUnlockTaskError = ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void EnsureElevatedStartupTask(string? executablePath = null)
    {
        LastElevatedTaskError = null;
        executablePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("无法确定当前程序路径，未能注册管理员模式启动任务。");
        }

        if (string.Equals(GetElevatedTaskCommand(), executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var xmlPath = Path.Combine(Path.GetTempPath(), "QuickTranslate-elevated-startup.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildElevatedStartupTaskXml(executablePath), Encoding.Unicode);
            var (ok, output) = RunSchtasks($"/Create /TN {ElevatedTaskName} /XML \"{xmlPath}\" /F");
            if (!ok)
            {
                var message = string.IsNullOrWhiteSpace(output)
                    ? "任务计划程序拒绝了管理员模式启动任务。"
                    : output;
                LastElevatedTaskError = message;
                throw new InvalidOperationException(message);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastElevatedTaskError = ex.Message;
            throw;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void RemoveElevatedStartupTask()
    {
        LastElevatedTaskError = null;
        var (ok, output) = RunSchtasks($"/Delete /TN {ElevatedTaskName} /F");
        if (ok || GetElevatedTaskCommand() is null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(output)
            ? "无法删除管理员模式启动任务。"
            : output;
        LastElevatedTaskError = message;
        throw new InvalidOperationException(message);
    }

    public void RemoveUnlockTask()
    {
        LastUnlockTaskError = null;
        var (ok, output) = RunSchtasks($"/Delete /TN {UnlockTaskName} /F");
        if (!ok && GetUnlockTaskCommand() is not null)
        {
            LastUnlockTaskError = string.IsNullOrWhiteSpace(output) ? "无法删除解锁后自动启动任务。" : output;
        }
    }

    private static string? GetUnlockTaskCommand()
    {
        var (ok, output) = RunSchtasks($"/Query /TN {UnlockTaskName} /XML");
        if (!ok || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            output, "<Command>(?<path>[^<]+)</Command>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success
            ? System.Net.WebUtility.HtmlDecode(match.Groups["path"].Value).Trim().Trim('"')
            : string.Empty;
    }

    private static string? GetElevatedTaskCommand()
    {
        var (ok, output) = RunSchtasks($"/Query /TN {ElevatedTaskName} /XML");
        if (!ok || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            output, "<Command>(?<path>[^<]+)</Command>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success
            ? System.Net.WebUtility.HtmlDecode(match.Groups["path"].Value).Trim().Trim('"')
            : string.Empty;
    }

    private static string BuildUnlockTaskXml(string executablePath)
    {
        var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";
        static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>QuickTranslate 在解锁后回到后台运行，用于覆盖睡眠/休眠唤醒的场景。</Description>
  </RegistrationInfo>
  <Triggers>
    <SessionStateChangeTrigger>
      <Enabled>true</Enabled>
      <UserId>{Escape(userId)}</UserId>
      <StateChange>SessionUnlock</StateChange>
    </SessionStateChangeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{Escape(userId)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Escape(executablePath)}</Command>
      <Arguments>--background</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string BuildElevatedStartupTaskXml(string executablePath)
    {
        var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";
        static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>QuickTranslate 以管理员权限在登录和解锁后启动，用于读取管理员窗口中的选区。</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{Escape(userId)}</UserId>
    </LogonTrigger>
    <SessionStateChangeTrigger>
      <Enabled>true</Enabled>
      <UserId>{Escape(userId)}</UserId>
      <StateChange>SessionUnlock</StateChange>
    </SessionStateChangeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{Escape(userId)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Escape(executablePath)}</Command>
      <Arguments>--background</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static (bool Ok, string Output) RunSchtasks(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, string.Empty);
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                return (false, "任务计划程序没有响应。");
            }
            return (process.ExitCode == 0, output.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, ex.Message);
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

    private static string BuildCommand(string executablePath) => $"\"{executablePath}\" --background";

    private static string? ExtractExecutablePath(string command)
    {
        var value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        // Older registrations may be unquoted. Pick the executable token and
        // leave any trailing launch arguments out of the comparison.
        var extensionIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (extensionIndex >= 0)
        {
            return value[..(extensionIndex + 4)].Trim();
        }

        var argumentIndex = value.IndexOf(' ');
        return (argumentIndex >= 0 ? value[..argumentIndex] : value).Trim();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}

public sealed record StartupRegistration(object? Value, RegistryValueKind Kind);

public enum StartupRegistrationState
{
    NotRegistered,
    CurrentExecutable,
    StalePath
}

public sealed record StartupStatus(StartupRegistrationState State, string? RegisteredExecutablePath)
{
    public bool IsRegistered => State != StartupRegistrationState.NotRegistered;
    public bool MatchesCurrentExecutable => State == StartupRegistrationState.CurrentExecutable;
}

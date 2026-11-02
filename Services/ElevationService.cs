using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuickTranslate.Services;

public enum ProcessElevation
{
    Unknown,
    Standard,
    Elevated
}

public sealed record ElevationRestartResult(bool Started, string? Error);

public sealed class ProtectedWindowCaptureException(string message) : InvalidOperationException(message);

/// <summary>
/// Small Win32 wrapper for the integrity boundary that prevents a normal process
/// from sending synthetic input to an elevated window.
/// </summary>
public static class ElevationService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int ErrorAccessDenied = 5;

    public static bool IsCurrentProcessElevated() => IsProcessElevated(GetCurrentProcess());

    public static bool IsExecutableInProtectedLocation()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var path = Path.GetFullPath(executablePath);
            return IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
                   IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static ProcessElevation GetWindowElevation(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return ProcessElevation.Unknown;
        }

        try
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return ProcessElevation.Unknown;
            }

            if (processId == (uint)Environment.ProcessId)
            {
                return IsCurrentProcessElevated() ? ProcessElevation.Elevated : ProcessElevation.Standard;
            }

            var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                // A normal process usually cannot inspect an elevated process token.
                return error == ErrorAccessDenied ? ProcessElevation.Elevated : ProcessElevation.Unknown;
            }

            try
            {
                if (!OpenProcessToken(process, TokenQuery, out var token))
                {
                    var error = Marshal.GetLastWin32Error();
                    return error == ErrorAccessDenied ? ProcessElevation.Elevated : ProcessElevation.Unknown;
                }

                try
                {
                    if (!GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int),
                            out _))
                    {
                        return ProcessElevation.Unknown;
                    }

                    return elevated != 0 ? ProcessElevation.Elevated : ProcessElevation.Standard;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return ProcessElevation.Unknown;
        }
    }

    public static string? GetWindowProcessName(IntPtr window)
    {
        try
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public static bool CanSendInputTo(IntPtr window) =>
        IsCurrentProcessElevated() || GetWindowElevation(window) != ProcessElevation.Elevated;

    public static ElevationRestartResult TryRestartElevated()
    {
        if (IsCurrentProcessElevated())
        {
            return new ElevationRestartResult(false, "QuickTranslate 已经是管理员权限。");
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new ElevationRestartResult(false, "无法确定 QuickTranslate 的程序路径。");
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                // The new instance waits for this process to exit before registering
                // the single-instance key, then continues in the tray.
                Arguments = $"--background --elevated-restart --replace-pid {Environment.ProcessId}"
            };

            using var process = Process.Start(startInfo);
            return process is null
                ? new ElevationRestartResult(false, "系统没有启动新的 QuickTranslate 实例。")
                : new ElevationRestartResult(true, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevationRestartResult(false, "已取消管理员权限请求。");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            return new ElevationRestartResult(false, ex.Message);
        }
    }

    private static bool IsProcessElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int), out _) &&
                   elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information,
        int informationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

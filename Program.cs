using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.Activation;

namespace QuickTranslate;

internal static class Program
{
    private static readonly object ActivationLock = new();
    private static App? _app;
    private static bool _openRequested;

    internal static bool StartInBackground { get; private set; }
    internal static bool AskOnStart { get; private set; }
    internal static bool ElevatedRestart { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            StartInBackground = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            AskOnStart = args.Contains("--ask", StringComparer.OrdinalIgnoreCase);
            ElevatedRestart = args.Contains("--elevated-restart", StringComparer.OrdinalIgnoreCase);
            WaitForReplacedInstance(args);
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var instance = AppInstance.FindOrRegisterForKey("QuickTranslate");
            if (!instance.IsCurrent)
            {
                if (ElevatedRestart)
                {
                    MessageBox(IntPtr.Zero,
                        "管理员模式已启动，但旧的 QuickTranslate 实例没有及时退出。\n" +
                        "请从托盘退出旧实例后重新启动 QuickTranslate。",
                        "QuickTranslate", 0x10);
                    return 0;
                }

                AllowSetForegroundWindow((uint)instance.ProcessId);
                RedirectActivation(activation, instance);
                return 0;
            }

            instance.Activated += OnActivated;
            try
            {
                Application.Start(parameters =>
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                    _ = new App();
                });
            }
            finally
            {
                lock (ActivationLock)
                {
                    _app = null;
                }
                instance.Activated -= OnActivated;
                instance.UnregisterKey();
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, $"QuickTranslate 无法启动：\n{ex.Message}", "QuickTranslate", 0x10);
            return 1;
        }
    }

    private static void WaitForReplacedInstance(string[] args)
    {
        var processId = ParseIntOption(args, "--replace-pid");
        if (processId is not int id || id <= 0 || id == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(id);
            previous.WaitForExit(10000);
            // Give the old instance a moment to release the single-instance key.
            Thread.Sleep(150);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // The old process has already exited, which is the normal case.
        }
    }

    private static int? ParseIntOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return int.TryParse(args[index + 1], out var value) ? value : null;
            }

            var prefix = name + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(argument[prefix.Length..], out var inlineValue))
            {
                return inlineValue;
            }
        }

        return null;
    }

    internal static bool AttachApp(App app)
    {
        lock (ActivationLock)
        {
            _app = app;
            var openRequested = _openRequested;
            _openRequested = false;
            return openRequested;
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        if (IsBackgroundActivation(args))
        {
            return;
        }

        App? app;
        lock (ActivationLock)
        {
            app = _app;
            if (app is null)
            {
                _openRequested = true;
            }
        }
        if (HasArgument(args, "--ask"))
        {
            app?.ShowQuickAskWindow();
        }
        else
        {
            app?.ShowMainWindow();
        }
    }

    private static bool HasArgument(AppActivationArguments args, string expected)
    {
        if (args.Data is not ILaunchActivatedEventArgs launch || string.IsNullOrWhiteSpace(launch.Arguments))
        {
            return false;
        }

        var arguments = CommandLineToArgv(launch.Arguments, out var count);
        if (arguments == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            for (var index = 0; index < count; index++)
            {
                var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size));
                if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            LocalFree(arguments);
        }
    }

    private static bool IsBackgroundActivation(AppActivationArguments args) => HasArgument(args, "--background");

    private static void RedirectActivation(AppActivationArguments args, AppInstance instance)
    {
        using var completed = new ManualResetEvent(false);
        var redirectTask = Task.Run(async () =>
        {
            try
            {
                await instance.RedirectActivationToAsync(args);
            }
            finally
            {
                completed.Set();
            }
        });

        // Pump COM while waiting; a blocking wait on the STA can deadlock redirection.
        var result = CoWaitForMultipleObjects(0, uint.MaxValue, 1,
            [completed.SafeWaitHandle.DangerousGetHandle()], out _);
        Marshal.ThrowExceptionForHR(result);
        redirectTask.GetAwaiter().GetResult();
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint milliseconds, uint count,
        IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}

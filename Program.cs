using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.Activation;

namespace QuickTranslate;

internal static class Program
{
    private static readonly object ActivationLock = new();
    private static App? _app;
    private static bool _openRequested;

    internal static bool StartInBackground { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            StartInBackground = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var instance = AppInstance.FindOrRegisterForKey("QuickTranslate");
            if (!instance.IsCurrent)
            {
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
        app?.ShowMainWindow();
    }

    private static bool IsBackgroundActivation(AppActivationArguments args)
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
                if (string.Equals(value, "--background", StringComparison.OrdinalIgnoreCase))
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

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QuickTranslate.Services;

namespace QuickTranslate;

public partial class App : Application
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public static new App Current => (App)Application.Current;

    public SettingsService Settings { get; } = new();

    public TargetLanguageService TargetLanguage { get; }

    public TranslationCacheService Cache { get; } = new();

    public TranslationHistoryService History { get; } = new();

    public MagpieTranslationService Translator { get; }

    public SelectionCaptureService SelectionCapture { get; } = new();

    public GlobalHotkeyService GlobalHotkey { get; } = new();

    public StartupService Startup { get; } = new();

    public MainWindow? MainWindow { get; private set; }

    private int _elevationRestartRequested;

    public App()
    {
        TargetLanguage = new TargetLanguageService(Settings);
        Translator = new MagpieTranslationService(Cache, History);
        UnhandledException += (_, e) => LogCrash("XAML", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("CLR", e.ExceptionObject as Exception);
        InitializeComponent();
    }

    // A tray app that dies silently is hard to diagnose; keep a small local log of fatal errors.
    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickTranslate");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        if (Program.ElevatedRestart)
        {
            MainWindow.ShowElevatedRestartNotice();
        }

        MainWindow.Closed += (_, _) => Exit();
        RepairStartupRegistration();
        var openRequested = Program.AttachApp(this);
        if (!Program.StartInBackground || !MainWindow.IsTrayAvailable || openRequested)
        {
            MainWindow.ShowMainWindow();
        }
    }

    // Keeps the unlock task pointing at the current executable after the app is moved or republished.
    private void RepairStartupRegistration()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var settings = Settings.Load();
                if (settings.StartWithWindows &&
                    (!settings.UseAdministratorMode || ElevationService.IsCurrentProcessElevated()))
                {
                    Startup.EnsureConfigured(true, settings.UseAdministratorMode);
                }
            }
            catch (Exception)
            {
                // Startup repair is best effort; the settings page surfaces real failures.
            }
        });
    }

    internal void ShowMainWindow() => _dispatcherQueue.TryEnqueue(() => MainWindow?.ShowMainWindow());

    internal async Task<ElevationRestartResult> RequestElevatedRestartAsync()
    {
        if (Interlocked.Exchange(ref _elevationRestartRequested, 1) != 0)
        {
            return new ElevationRestartResult(false, "正在请求管理员权限，请稍候。");
        }

        var result = await Task.Run(ElevationService.TryRestartElevated);
        if (result.Started)
        {
            MainWindow?.ExitForRestart();
        }
        else
        {
            Interlocked.Exchange(ref _elevationRestartRequested, 0);
        }

        return result;
    }
}

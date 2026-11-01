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

    public MagpieTranslationService Translator { get; } = new();

    public SelectionCaptureService SelectionCapture { get; } = new();

    public GlobalHotkeyService GlobalHotkey { get; } = new();

    public StartupService Startup { get; } = new();

    public MainWindow? MainWindow { get; private set; }

    public App()
    {
        TargetLanguage = new TargetLanguageService(Settings);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Closed += (_, _) => Exit();
        var openRequested = Program.AttachApp(this);
        if (!Program.StartInBackground || !MainWindow.IsTrayAvailable || openRequested)
        {
            MainWindow.ShowMainWindow();
        }
    }

    internal void ShowMainWindow() => _dispatcherQueue.TryEnqueue(() => MainWindow?.ShowMainWindow());
}

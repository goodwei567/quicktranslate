namespace QuickTranslate.Models;

public sealed class AppSettings
{
    public string MagpieBaseUrl { get; set; } = "http://127.0.0.1:3425/v1";

    public string ModelId { get; set; } = "auto/gemini-3.8-flash";

    public string TargetLanguage { get; set; } = "中文";

    public string TaskPrompt { get; set; } = TaskPrompts.Translation;

    public uint HotkeyModifiers { get; set; } = HotkeyGesture.Control | HotkeyGesture.Shift;

    public uint HotkeyVirtualKey { get; set; } = 0x54;

    // Free-form assistant: opens an empty composer instead of reading the selection.
    public uint AskHotkeyModifiers { get; set; } = HotkeyGesture.Control | HotkeyGesture.Shift;

    public uint AskHotkeyVirtualKey { get; set; } = 0x5A;

    public bool StartWithWindows { get; set; }

    // Opt-in elevation is only used when the user needs to capture text from
    // an elevated window (for example, VS Code launched as administrator).
    public bool UseAdministratorMode { get; set; }

    public bool AutoElevateForProtectedApps { get; set; } = true;

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool EnableStreaming { get; set; } = true;

    public bool EnableTranslationCache { get; set; } = true;

    public bool EnableTranslationHistory { get; set; } = true;

    public int HistoryLimit { get; set; } = 200;

    // Empty means "let the provider decide". Values are stored per model because
    // Magpie can route different models with different reasoning capabilities.
    public Dictionary<string, string> ReasoningEffortByModel { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

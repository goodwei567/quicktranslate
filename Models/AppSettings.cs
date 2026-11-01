namespace QuickTranslate.Models;

public sealed class AppSettings
{
    public string MagpieBaseUrl { get; set; } = "http://127.0.0.1:3425/v1";

    public string ModelId { get; set; } = "auto/gemini-3.8-flash";

    public string TargetLanguage { get; set; } = "中文";

    public string TaskPrompt { get; set; } = TaskPrompts.Translation;

    public uint HotkeyModifiers { get; set; } = HotkeyGesture.Control | HotkeyGesture.Shift;

    public uint HotkeyVirtualKey { get; set; } = 0x54;

    public bool StartWithWindows { get; set; }

    public bool MinimizeToTrayOnClose { get; set; } = true;
}

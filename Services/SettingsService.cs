using QuickTranslate.Models;
using System.Text.Json;

namespace QuickTranslate.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public event EventHandler? Saved;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickTranslate",
            "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                var root = document.RootElement;
                var hasLegacyApiKey = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("GeminiApiKey", out _);
                var modelId = ReadString(root, "ModelId");
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    // Migrate the last Gemini-specific setting without reading or retaining its old API key.
                    modelId = ReadString(root, "GeminiModel");
                }

                var settings = new AppSettings
                {
                    MagpieBaseUrl = NormalizeBaseUrl(ReadString(root, "MagpieBaseUrl")),
                    ModelId = string.IsNullOrWhiteSpace(modelId) || modelId.Equals("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase)
                        ? "auto/gemini-3.8-flash"
                        : modelId.Trim(),
                    TargetLanguage = ReadString(root, "TargetLanguage") is { Length: > 0 } language
                        ? language.Trim()
                        : "中文",
                    TaskPrompt = TaskPrompts.Normalize(ReadString(root, "TaskPrompt")),
                    HotkeyModifiers = ReadUInt32(root, "HotkeyModifiers", HotkeyGesture.Default.Modifiers),
                    HotkeyVirtualKey = ReadUInt32(root, "HotkeyVirtualKey", HotkeyGesture.Default.VirtualKey),
                    StartWithWindows = ReadBoolean(root, "StartWithWindows", false),
                    MinimizeToTrayOnClose = ReadBoolean(root, "MinimizeToTrayOnClose", true)
                };
                var hotkey = HotkeyGesture.FromSettings(settings);
                settings.HotkeyModifiers = hotkey.Modifiers;
                settings.HotkeyVirtualKey = hotkey.VirtualKey;

                // Rewrite once if legacy settings still contain a direct Gemini API key.
                if (hasLegacyApiKey)
                {
                    Save(settings);
                }

                return settings;
            }
        }
        catch (JsonException)
        {
            // Fall back to defaults when a previous settings file is incomplete.
        }
        catch (IOException)
        {
            // A locked or unavailable file should not prevent the app from starting.
        }
        catch (UnauthorizedAccessException)
        {
            // Report write permissions when saving, rather than failing during startup.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var hotkey = HotkeyGesture.FromSettings(settings);
        var normalized = new AppSettings
        {
            MagpieBaseUrl = NormalizeBaseUrl(settings.MagpieBaseUrl),
            ModelId = string.IsNullOrWhiteSpace(settings.ModelId)
                ? "auto/gemini-3.8-flash"
                : settings.ModelId.Trim(),
            TargetLanguage = string.IsNullOrWhiteSpace(settings.TargetLanguage)
                ? "中文"
                : settings.TargetLanguage.Trim(),
            TaskPrompt = TaskPrompts.Normalize(settings.TaskPrompt),
            HotkeyModifiers = hotkey.Modifiers,
            HotkeyVirtualKey = hotkey.VirtualKey,
            StartWithWindows = settings.StartWithWindows,
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose
        };

        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static uint ReadUInt32(JsonElement root, string propertyName, uint fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number)
            ? number
            : fallback;

    private static bool ReadBoolean(JsonElement root, string propertyName, bool fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static string NormalizeBaseUrl(string? value)
    {
        var url = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:3425/v1" : value.Trim();
        return url.TrimEnd('/');
    }
}

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
                    MinimizeToTrayOnClose = ReadBoolean(root, "MinimizeToTrayOnClose", true),
                    EnableStreaming = ReadBoolean(root, "EnableStreaming", true),
                    EnableTranslationCache = ReadBoolean(root, "EnableTranslationCache", true),
                    EnableTranslationHistory = ReadBoolean(root, "EnableTranslationHistory", true),
                    HistoryLimit = Math.Clamp(ReadInt32(root, "HistoryLimit", 200), 20, 1000),
                    ReasoningEffortByModel = ReadReasoningEfforts(root)
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
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
            EnableStreaming = settings.EnableStreaming,
            EnableTranslationCache = settings.EnableTranslationCache,
            EnableTranslationHistory = settings.EnableTranslationHistory,
            HistoryLimit = Math.Clamp(settings.HistoryLimit, 20, 1000),
            ReasoningEffortByModel = NormalizeReasoningEfforts(settings.ReasoningEffortByModel)
        };

        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        AtomicFile.WriteAllText(_settingsPath, json);
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

    private static int ReadInt32(JsonElement root, string propertyName, int fallback) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static Dictionary<string, string> ReadReasoningEfforts(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ReasoningEffortByModel", out var values) ||
            values.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in values.EnumerateObject())
        {
            var modelId = property.Name.Trim();
            var effort = property.Value.ValueKind == JsonValueKind.String
                ? NormalizeReasoningEffort(property.Value.GetString())
                : null;
            if (modelId.Length > 0 && effort is not null)
            {
                result[modelId] = effort;
            }
        }

        return result;
    }

    private static Dictionary<string, string> NormalizeReasoningEfforts(Dictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return result;
        }

        foreach (var (modelId, value) in values)
        {
            var normalizedModelId = modelId.Trim();
            var effort = NormalizeReasoningEffort(value);
            if (normalizedModelId.Length > 0 && effort is not null)
            {
                result[normalizedModelId] = effort;
            }
        }

        return result;
    }

    private static string? NormalizeReasoningEffort(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" ? normalized : null;
    }

    private static string NormalizeBaseUrl(string? value)
    {
        var url = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:3425/v1" : value.Trim();
        return url.TrimEnd('/');
    }
}

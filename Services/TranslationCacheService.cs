using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class TranslationCacheService
{
    private const int MaximumEntries = 250;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private readonly string _cachePath;
    private readonly object _gate = new();
    private Dictionary<string, CacheEntry>? _entries;

    public TranslationCacheService(string? cachePath = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickTranslate",
            "translation-cache.json");
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                PruneExpired();
                return _entries?.Count ?? 0;
            }
        }
    }

    public bool TryGet(AppSettings settings, string sourceText, out string result)
    {
        result = string.Empty;
        if (!settings.EnableTranslationCache || string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        lock (_gate)
        {
            PruneExpired();
            var key = BuildKey(settings, sourceText);
            if (_entries is not null &&
                _entries.TryGetValue(key, out var entry) &&
                DateTimeOffset.UtcNow - entry.LastUsedAt <= MaximumAge &&
                !string.IsNullOrWhiteSpace(entry.Result))
            {
                entry.LastUsedAt = DateTimeOffset.UtcNow;
                result = entry.Result;
                return true;
            }

            return false;
        }
    }

    public void Store(AppSettings settings, string sourceText, string result)
    {
        if (!settings.EnableTranslationCache ||
            string.IsNullOrWhiteSpace(sourceText) ||
            string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        lock (_gate)
        {
            PruneExpired();
            _entries ??= new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            _entries[BuildKey(settings, sourceText)] = new CacheEntry
            {
                Result = result,
                CreatedAt = now,
                LastUsedAt = now
            };

            while (_entries.Count > MaximumEntries)
            {
                var oldest = _entries.MinBy(pair => pair.Value.LastUsedAt);
                if (oldest.Key is null)
                {
                    break;
                }
                _entries.Remove(oldest.Key);
            }

            TrySave();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            AtomicFile.WriteAllText(_cachePath, "{}");
        }
    }

    private void EnsureLoaded()
    {
        if (_entries is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(_cachePath))
            {
                _entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                    File.ReadAllText(_cachePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _entries ??= new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
    }

    private void PruneExpired()
    {
        EnsureLoaded();
        var cutoff = DateTimeOffset.UtcNow - MaximumAge;
        var entries = _entries!;
        foreach (var key in entries
                     .Where(pair => pair.Value.LastUsedAt < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(key);
        }
    }

    private void TrySave()
    {
        try
        {
            AtomicFile.WriteAllText(_cachePath, JsonSerializer.Serialize(_entries, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string BuildKey(AppSettings settings, string sourceText)
    {
        var modelId = settings.ModelId.Trim();
        var reasoning = settings.ReasoningEffortByModel is { } efforts &&
            efforts.TryGetValue(modelId, out var effort)
                ? effort
                : string.Empty;
        var raw = string.Join('\u001F',
            settings.MagpieBaseUrl.Trim().TrimEnd('/'),
            modelId,
            settings.TargetLanguage.Trim(),
            TaskPrompts.Normalize(settings.TaskPrompt),
            reasoning,
            sourceText.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    internal sealed class CacheEntry
    {
        public string Result { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastUsedAt { get; set; }
    }
}

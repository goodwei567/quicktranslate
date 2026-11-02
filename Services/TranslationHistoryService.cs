using System.Text.Json;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class TranslationHistoryService
{
    private readonly string _historyPath;
    private readonly object _gate = new();
    private List<TranslationHistoryEntry>? _entries;

    public TranslationHistoryService(string? historyPath = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickTranslate",
            "history.json");
    }

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _entries!.Count;
            }
        }
    }

    public IReadOnlyList<TranslationHistoryEntry> GetEntries()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries!
                .OrderByDescending(entry => entry.CreatedAt)
                .Select(entry => entry.Clone())
                .ToArray();
        }
    }

    public TranslationHistoryEntry? GetEntry(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _entries!.FirstOrDefault(entry => entry.Id == id)?.Clone();
        }
    }

    /// <summary>
    /// Finds the record saved for a finished request so callers can favorite it from another window.
    /// </summary>
    public string? FindEntryId(AppSettings settings, string sourceText)
    {
        if (!settings.EnableTranslationHistory || string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        lock (_gate)
        {
            EnsureLoaded();
            var modelId = settings.ModelId.Trim();
            var prompt = TaskPrompts.Normalize(settings.TaskPrompt);
            var normalizedSource = sourceText.Trim();
            return _entries!.FirstOrDefault(entry =>
                string.Equals(entry.SourceText, normalizedSource, StringComparison.Ordinal) &&
                string.Equals(entry.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetLanguage, settings.TargetLanguage.Trim(), StringComparison.Ordinal) &&
                string.Equals(entry.TaskPrompt, prompt, StringComparison.Ordinal))?.Id;
        }
    }

    public void Add(AppSettings settings, string sourceText, string result)
    {
        if (!settings.EnableTranslationHistory ||
            string.IsNullOrWhiteSpace(sourceText) ||
            string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();
            var modelId = settings.ModelId.Trim();
            var prompt = TaskPrompts.Normalize(settings.TaskPrompt);
            var normalizedSource = sourceText.Trim();
            bool Matches(TranslationHistoryEntry entry) =>
                string.Equals(entry.SourceText, normalizedSource, StringComparison.Ordinal) &&
                string.Equals(entry.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.TargetLanguage, settings.TargetLanguage.Trim(), StringComparison.Ordinal) &&
                string.Equals(entry.TaskPrompt, prompt, StringComparison.Ordinal);

            var entries = _entries!;
            var existing = entries.FirstOrDefault(Matches);
            if (existing is not null)
            {
                entries.Remove(existing);
                existing.ResultText = result;
                existing.CreatedAt = DateTimeOffset.Now;
                entries.Insert(0, existing);
            }
            else
            {
                entries.Insert(0, new TranslationHistoryEntry
                {
                    SourceText = normalizedSource,
                    ResultText = result,
                    ModelId = modelId,
                    TargetLanguage = settings.TargetLanguage.Trim(),
                    TaskPrompt = prompt,
                    CreatedAt = DateTimeOffset.Now
                });
            }

            var limit = Math.Clamp(settings.HistoryLimit, 20, 1000);
            while (entries.Count > limit)
            {
                var removable = entries.LastOrDefault(entry => !entry.IsFavorite);
                if (removable is null)
                {
                    break;
                }
                entries.Remove(removable);
            }

            TrySave();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetFavorite(string id, bool isFavorite)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var entry = _entries!.FirstOrDefault(item => item.Id == id);
            if (entry is null || entry.IsFavorite == isFavorite)
            {
                return;
            }

            entry.IsFavorite = isFavorite;
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries!.RemoveAll(entry => entry.Id == id) == 0)
            {
                return;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            AtomicFile.WriteAllText(_historyPath, "[]");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoaded()
    {
        if (_entries is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(_historyPath))
            {
                _entries = JsonSerializer.Deserialize<List<TranslationHistoryEntry>>(
                    File.ReadAllText(_historyPath),
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

        _entries ??= [];
    }

    private void TrySave()
    {
        try
        {
            Save();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Save()
    {
        AtomicFile.WriteAllText(_historyPath, JsonSerializer.Serialize(_entries, new JsonSerializerOptions
        {
            WriteIndented = false
        }));
    }
}

using System.Collections.ObjectModel;

namespace QuickTranslate.Services;

public sealed class TargetLanguageService
{
    private readonly SettingsService _settings;
    private readonly ObservableCollection<string> _languages =
    [
        "中文", "繁體中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语",
        "俄语", "葡萄牙语", "意大利语", "阿拉伯语", "泰语", "越南语", "印地语", "印尼语", "土耳其语"
    ];

    public string CurrentLanguage { get; private set; }
    public ReadOnlyObservableCollection<string> Languages { get; }
    public event EventHandler? Changed;

    public TargetLanguageService(SettingsService settings)
    {
        _settings = settings;
        CurrentLanguage = Normalize(settings.Load().TargetLanguage);
        IncludeLanguage(CurrentLanguage);
        Languages = new ReadOnlyObservableCollection<string>(_languages);
    }

    public void SetLanguage(string language)
    {
        var normalized = Normalize(language);
        if (normalized == CurrentLanguage)
        {
            return;
        }

        // Save only the language, without overwriting saved connection/model preferences.
        var settings = _settings.Load();
        settings.TargetLanguage = normalized;
        _settings.Save(settings);
        IncludeLanguage(normalized);
        CurrentLanguage = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void IncludeLanguage(string language)
    {
        if (!_languages.Contains(language))
        {
            _languages.Add(language);
        }
    }

    private static string Normalize(string language) => string.IsNullOrWhiteSpace(language) ? "中文" : language.Trim();
}

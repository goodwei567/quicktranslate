namespace QuickTranslate.Models;

public sealed record MagpieModel(string Id, string DisplayName, string Provider)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Provider) || Provider == "unknown"
        ? DisplayName
        : $"{DisplayName} ({Provider})";
}

using System.Text.RegularExpressions;

namespace QuickTranslate.Models;

public sealed class TranslationHistoryEntry
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex MarkdownNoise = new(@"[#*_`>\[\]\(\)]", RegexOptions.Compiled);

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string SourceText { get; set; } = string.Empty;
    public string ResultText { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "中文";
    public string TaskPrompt { get; set; } = TaskPrompts.Translation;
    public bool IsFavorite { get; set; }

    public string TaskLabel => TaskPrompts.GetActionName(TaskPrompt);
    public string ModelLabel => string.IsNullOrWhiteSpace(ModelId)
        ? "未知模型"
        : ModelId.Split('/').LastOrDefault() ?? ModelId;
    public string MetaText => $"{CreatedAt.LocalDateTime:MM-dd HH:mm} · {TaskLabel} · {TargetLanguage} · {ModelLabel}";
    public string SourcePreview => Preview(SourceText, 160);
    public string ResultPreview => Preview(MarkdownNoise.Replace(ResultText, string.Empty), 260);
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteLabel => IsFavorite ? "取消收藏" : "收藏";

    public TranslationHistoryEntry Clone() => new()
    {
        Id = Id,
        CreatedAt = CreatedAt,
        SourceText = SourceText,
        ResultText = ResultText,
        ModelId = ModelId,
        TargetLanguage = TargetLanguage,
        TaskPrompt = TaskPrompt,
        IsFavorite = IsFavorite
    };

    private static string Preview(string value, int maxLength)
    {
        var normalized = Whitespace.Replace(value, " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }
}

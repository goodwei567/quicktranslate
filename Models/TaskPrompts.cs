namespace QuickTranslate.Models;

public static class TaskPrompts
{
    public const string Translation = "请将选中文字翻译为{language}。保留原意、语气、段落和格式，只输出译文，不要添加前言或解释。";

    public const string Explanation = "请用{language}解释选中文字，而不是仅仅翻译。先概括核心意思，再说明关键概念、逻辑和必要背景；遇到专业术语或难懂表达时，用通俗的说法和简短例子解释。对无法从原文确定的内容，请明确说明，不要编造。";

    public const string Summary = "请用{language}总结选中文字。先用一句话概括主题，再列出关键要点；保留重要的事实、数字和结论，不要添加原文中没有的信息。";

    public static string Normalize(string? prompt) => string.IsNullOrWhiteSpace(prompt) ? Translation : prompt.Trim();

    public static string GetActionName(string? prompt) => Normalize(prompt) switch
    {
        Translation => "翻译",
        Explanation => "解释",
        Summary => "总结",
        _ => "处理"
    };

    public static string GetResultLabel(string? prompt) => Normalize(prompt) switch
    {
        Translation => "译文",
        Explanation => "解释",
        Summary => "摘要",
        _ => "结果"
    };

    public static string Expand(string? prompt, string language) =>
        Normalize(prompt).Replace("{language}", language, StringComparison.Ordinal);
}

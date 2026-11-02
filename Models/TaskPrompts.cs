namespace QuickTranslate.Models;

public static class TaskPrompts
{
    public const string Translation = "请将选中文字翻译为{language}。保留原意、语气、段落和格式，只输出译文，不要添加前言或解释。";

    public const string Explanation = "请用{language}解释选中文字，而不是仅仅翻译。先概括核心意思，再说明关键概念、逻辑和必要背景；遇到专业术语或难懂表达时，用通俗的说法和简短例子解释。对无法从原文确定的内容，请明确说明，不要编造。";

    public const string Summary = "请用{language}总结选中文字。先用一句话概括主题，再列出关键要点；保留重要的事实、数字和结论，不要添加原文中没有的信息。";

    public const string Technical = "请将选中文字中的自然语言翻译为{language}，同时严格保留技术内容：代码块、行内代码、命令、变量名、函数名、类名、API 名称、文件路径、URL、JSON/YAML 键名和值、日志、数字及单位不得翻译或改写。保持原有 Markdown、缩进、标点和段落结构；代码注释只在带有明确自然语言含义时翻译，并继续保留为注释。只输出处理后的正文，不要添加前言、总结或额外解释。";

    /// <summary>
    /// Quick tasks offered in the popup. A null prompt marks the user's own prompt from settings.
    /// </summary>
    public static IReadOnlyList<TaskPreset> Presets { get; } =
    [
        new("翻译", Translation),
        new("技术文档", Technical),
        new("解释文字", Explanation),
        new("总结要点", Summary),
        new("自定义", null)
    ];

    public static string Normalize(string? prompt) => string.IsNullOrWhiteSpace(prompt) ? Translation : prompt.Trim();

    public static string GetActionName(string? prompt) => Normalize(prompt) switch
    {
        Translation => "翻译",
        Technical => "技术翻译",
        Explanation => "解释",
        Summary => "总结",
        _ => "处理"
    };

    public static string GetResultLabel(string? prompt) => Normalize(prompt) switch
    {
        Translation => "译文",
        Technical => "译文",
        Explanation => "解释",
        Summary => "摘要",
        _ => "结果"
    };

    public static string Expand(string? prompt, string language) =>
        Normalize(prompt).Replace("{language}", language, StringComparison.Ordinal);
}

public sealed record TaskPreset(string Label, string? Prompt);

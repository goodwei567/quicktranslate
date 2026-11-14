using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace QuickTranslate.Services;

internal sealed record MathFormula(string LaTeX, bool IsDisplay);
internal sealed record MathMarkdownResult(string Markdown, IReadOnlyDictionary<string, MathFormula> Formulas);

internal static class MathMarkdownProcessor
{
    internal const string ImagePrefix = "qtmath://formula/";
    internal const int MaxFormulaLength = 2048;
    private const int MaxFormulas = 128;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();

    public static MathMarkdownResult Process(string source)
    {
        var formulas = new Dictionary<string, MathFormula>();
        if (source.IndexOfAny(['$', '\\']) < 0)
        {
            return new(source, formulas);
        }

        // Parse protected spans first so examples in code and link destinations stay literal.
        var document = Markdown.Parse(source, Pipeline);
        var protectedSpans = document.Descendants()
            .Where(node => node is CodeBlock or CodeInline or LinkInline or AutolinkInline or HtmlBlock)
            .Select(node => node.Span)
            .OrderBy(span => span.Start)
            .ToArray();
        var protectedIndex = 0;
        var output = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length;)
        {
            while (protectedIndex < protectedSpans.Length && protectedSpans[protectedIndex].End < index)
            {
                protectedIndex++;
            }
            if (protectedIndex < protectedSpans.Length && protectedSpans[protectedIndex].Start <= index)
            {
                var end = Math.Min(source.Length, protectedSpans[protectedIndex].End + 1);
                output.Append(source, index, end - index);
                index = end;
                continue;
            }

            if (formulas.Count >= MaxFormulas || IsEscaped(source, index) ||
                !TryReadFormula(source, index, out var formula, out var consumed))
            {
                output.Append(source[index++]);
                continue;
            }

            if (protectedIndex < protectedSpans.Length && protectedSpans[protectedIndex].Start < index + consumed)
            {
                output.Append(source[index++]);
                continue;
            }

            var url = ImagePrefix + formulas.Count;
            formulas.Add(url, formula!);
            if (formula!.IsDisplay)
            {
                output.Append("\n\n");
            }
            output.Append(ImageMarkdown(url, formula));
            if (formula.IsDisplay)
            {
                output.Append("\n\n");
            }
            index += consumed;
        }
        return new(output.ToString(), formulas);
    }

    public static string ImageMarkdown(string url, MathFormula formula)
    {
        var alt = formula.LaTeX.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("\r", " ").Replace("\n", " ");
        return "![" + alt + "](" + url + ")";
    }

    public static string LiteralMarkdown(MathFormula formula) =>
        "`" + formula.LaTeX.Replace("`", "").Replace("\r", " ").Replace("\n", " ") + "`";

    private static bool TryReadFormula(string text, int start, out MathFormula? formula, out int consumed)
    {
        formula = null;
        consumed = 0;
        string close;
        int delimiterLength;
        bool display;
        if (text.AsSpan(start).StartsWith("$$"))
        {
            close = "$$";
            delimiterLength = 2;
            display = true;
        }
        else if (text.AsSpan(start).StartsWith(@"\("))
        {
            close = @"\)";
            delimiterLength = 2;
            display = false;
        }
        else if (text.AsSpan(start).StartsWith(@"\["))
        {
            close = @"\]";
            delimiterLength = 2;
            display = true;
        }
        else if (text[start] == '$')
        {
            close = "$";
            delimiterLength = 1;
            display = false;
            if (start + 1 == text.Length || char.IsWhiteSpace(text[start + 1]))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var contentStart = start + delimiterLength;
        var limit = Math.Min(text.Length, contentStart + MaxFormulaLength + delimiterLength);
        for (var end = contentStart; end < limit; end++)
        {
            if (!display && text[end] is '\r' or '\n')
            {
                return false;
            }
            if (!text.AsSpan(end).StartsWith(close) || IsEscaped(text, end))
            {
                continue;
            }
            if (close == "$" && (char.IsWhiteSpace(text[end - 1]) ||
                end + 1 < text.Length && char.IsDigit(text[end + 1])))
            {
                continue;
            }

            var latex = text[contentStart..end].Trim();
            if (latex.Length == 0 || latex.Length > MaxFormulaLength ||
                close == "$" && decimal.TryParse(latex, out _))
            {
                return false;
            }
            // Bound nesting before handing model-generated input to the recursive typesetter.
            var depth = 0;
            for (var i = 0; i < latex.Length; i++)
            {
                if (!IsEscaped(latex, i) && latex[i] == '{' && ++depth > 48)
                {
                    return false;
                }
                if (!IsEscaped(latex, i) && latex[i] == '}')
                {
                    depth--;
                }
            }
            formula = new(latex, display);
            consumed = end + delimiterLength - start;
            return true;
        }
        // An unfinished streaming formula remains readable until its closing delimiter arrives.
        return false;
    }

    private static bool IsEscaped(string text, int index)
    {
        var count = 0;
        while (--index >= 0 && text[index] == '\\')
        {
            count++;
        }
        return count % 2 != 0;
    }
}

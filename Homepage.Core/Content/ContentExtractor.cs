using System.Text;
using Homepage.Blocks;

namespace Homepage.Content;

/// <summary>Plain text of a block tree, for search, excerpts and reading time.</summary>
public sealed class ContentExtractor(MarkdownRenderer markdown)
{
    public string ExtractText(BlockTree tree)
    {
        var sb = new StringBuilder();
        foreach (var n in tree.All())
        {
            var text = n.Type switch
            {
                "heading" => n.PropsAs<HeadingProps>().Text,
                "markdown" => markdown.ToPlainText(n.PropsAs<MarkdownProps>().Markdown),
                "quote" => n.PropsAs<QuoteProps>().Text,
                "button" => n.PropsAs<ButtonProps>().Text,
                "image" => n.PropsAs<ImageProps>().Caption,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) sb.Append(text.Trim()).Append('\n');
        }
        return sb.ToString().Trim();
    }

    public static string Excerpt(string text, int max = 240)
    {
        var flat = string.Join(' ', text.Split((char[])['\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= max) return flat;
        var cut = flat.LastIndexOf(' ', max);
        return flat[..(cut > max / 2 ? cut : max)] + "…";
    }

    public static int ReadingMinutes(string text) =>
        Math.Max(1, (int)Math.Round(text.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length / 220.0));
}

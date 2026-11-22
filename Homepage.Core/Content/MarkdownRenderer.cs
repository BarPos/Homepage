using Ganss.Xss;
using Markdig;

namespace Homepage.Content;

/// <summary>Markdown → sanitized HTML, the same pipeline for the public site, the editor canvas and previews.</summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly HtmlSanitizer _sanitizer = CreateSanitizer();

    public string ToHtml(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? "" : _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline));

    public string ToPlainText(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? "" : Markdown.ToPlainText(markdown, _pipeline).Trim();

    /// <summary>The raw HTML block (html: rw only) is sanitized too: no scripts, no event handlers, no javascript: URLs.</summary>
    public string SanitizeHtml(string? html) => string.IsNullOrEmpty(html) ? "" : _sanitizer.Sanitize(html);

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedAttributes.Add("class");
        s.AllowedAttributes.Add("id");
        s.AllowedSchemes.Add("mailto");
        s.AllowedSchemes.Add("tel");
        return s;
    }
}

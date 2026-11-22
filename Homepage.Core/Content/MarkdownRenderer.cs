using Ganss.Xss;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Homepage.Content;

/// <summary>Markdown → sanitized HTML, the same pipeline for the public site, the editor canvas and previews.</summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly HtmlSanitizer _sanitizer = CreateSanitizer();

    /// <param name="resolveLink">Turns references (page:12, …) into addresses; returns null for a missing target, whose link is then dropped (its text stays).</param>
    public string ToHtml(string? markdown, Func<string, string?>? resolveLink = null)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var doc = Markdown.Parse(markdown, _pipeline);
        MarkNewTabLinks(doc);
        if (resolveLink is not null)
            foreach (var link in doc.Descendants<LinkInline>().ToList())
            {
                if (link.Url is null || !Blocks.SafeUrlAttribute.TryParseReference(link.Url, out _, out _)) continue;
                var href = resolveLink(link.Url);
                if (href is not null) { link.Url = href; continue; }
                if (link.IsImage) { link.Remove(); continue; }
                // A link to something that is gone: keep the words, drop the link.
                var parent = link.Parent;
                if (parent is null) continue;
                foreach (var child in link.ToList()) { child.Remove(); link.InsertBefore(child); }
                link.Remove();
            }
        using var writer = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        return _sanitizer.Sanitize(writer.ToString());
    }

    /// <summary>
    /// Links that open in a new tab: [text](https://…){new} (or {blank}, or {target=_blank}). The marker becomes
    /// target="_blank"; the sanitizer adds rel="noopener noreferrer".
    /// </summary>
    private static void MarkNewTabLinks(MarkdownDocument doc)
    {
        foreach (var link in doc.Descendants<LinkInline>())
        {
            if (link.IsImage || link.TryGetAttributes() is not { } attrs) continue;
            // "\_blank": the rich text editor escapes the underscore.
            var marked = attrs.Properties?.RemoveAll(p => p.Key is "new" or "blank" || (p.Key == "target" && p.Value is "_blank" or "\\_blank")) > 0;
            if (attrs.Classes?.RemoveAll(c => c is "new" or "blank") > 0) marked = true;
            if (marked) attrs.AddProperty("target", "_blank");
        }
    }

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
        // No forms or controls in content: a hidden form on a public page could make a signed-in admin's click submit
        // an admin action (the site is one origin). No positioning, so content cannot cover the page with an overlay.
        foreach (var tag in new[] { "form", "input", "button", "select", "option", "optgroup", "textarea", "label", "fieldset", "legend", "datalist", "output" })
            s.AllowedTags.Remove(tag);
        foreach (var attr in new[] { "action", "formaction", "method", "formmethod", "for", "form" })
            s.AllowedAttributes.Remove(attr);
        foreach (var prop in new[] { "position", "z-index", "inset", "top", "left", "right", "bottom" })
            s.AllowedCssProperties.Remove(prop);
        // Links: only target="_blank" (new tab), always with rel="noopener noreferrer" so the new page cannot reach this one.
        s.AllowedAttributes.Add("target");
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is not AngleSharp.Dom.IElement el || el.TagName != "A") return;
            var target = el.GetAttribute("target");
            if (target is null) return;
            if (target == "_blank") el.SetAttribute("rel", "noopener noreferrer");
            else el.RemoveAttribute("target");
        };
        return s;
    }
}

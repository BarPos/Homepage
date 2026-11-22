using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Homepage.Content;

namespace Homepage.Mail;

/// <summary>
/// Turns a template (subject + Markdown body with {{placeholders}}) into an email: the placeholders are filled in
/// (values are escaped, so what a visitor typed cannot become a link or formatting), the Markdown becomes sanitized
/// HTML in a simple layout, and a plain-text version goes along.
/// </summary>
public sealed partial class EmailRenderer(MarkdownRenderer markdown)
{
    [GeneratedRegex(@"\{\{\s*([a-z][a-z0-9_.]*)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"([\\`*_{}\[\]()#+\-.!<>|~])")]
    private static partial Regex MarkdownSpecial();

    /// <summary>A value that goes into Markdown as plain text.</summary>
    public static string EscapeMarkdown(string value) => MarkdownSpecial().Replace(value, @"\$1");

    /// <summary>Values whose names end in ".url" (or are "answers") are inserted as they are; the rest is escaped.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values, bool markdownContext) =>
        Placeholder().Replace(template, m =>
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (!values.TryGetValue(name, out var v)) return "";
            if (!markdownContext || name.EndsWith(".url", StringComparison.Ordinal) || name == "answers") return v;
            return EscapeMarkdown(v);
        });

    public sealed record Rendered(string Subject, string Html, string Text);

    public Rendered Render(string subject, string bodyMarkdown, IReadOnlyDictionary<string, string> values, string siteTitle, string? unsubscribeUrl = null)
    {
        var subj = Fill(subject, values, markdownContext: false).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (subj.Length > 200) subj = subj[..200];
        var md = Fill(bodyMarkdown, values, markdownContext: true);
        var body = markdown.ToHtml(md);
        var html = Layout(body, siteTitle, unsubscribeUrl);
        var text = markdown.ToPlainText(md) + (unsubscribeUrl is null ? "" : $"\n\n--\nUnsubscribe: {unsubscribeUrl}");
        return new Rendered(subj, html, text);
    }

    /// <summary>Questions and answers as a Markdown list (answers escaped).</summary>
    public static string AnswersMarkdown(IEnumerable<(string Label, string Answer)> answers)
    {
        var sb = new StringBuilder();
        foreach (var (label, answer) in answers)
        {
            var a = answer.Length == 0 ? "—" : EscapeMarkdown(answer).Replace("\n", "  \n  ");
            sb.Append("- **").Append(EscapeMarkdown(label)).Append(":** ").Append(a).Append('\n');
        }
        return sb.ToString();
    }

    private static string Layout(string bodyHtml, string siteTitle, string? unsubscribeUrl)
    {
        var title = WebUtility.HtmlEncode(siteTitle);
        var footer = unsubscribeUrl is null ? "" :
            $"<p style=\"margin:16px 0 0;font-size:12px;color:#8a8f98\">You get this because you subscribed. <a href=\"{WebUtility.HtmlEncode(unsubscribeUrl)}\" style=\"color:#8a8f98\">Unsubscribe</a></p>";
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"></head>" +
               "<body style=\"margin:0;padding:24px 12px;background:#f4f5f7;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#1d2026;line-height:1.55\">" +
               "<div style=\"max-width:600px;margin:0 auto\">" +
               $"<div style=\"font-weight:700;font-size:15px;margin:0 0 12px 4px;color:#1d2026\">{title}</div>" +
               $"<div style=\"background:#ffffff;border:1px solid #e1e4ea;border-radius:12px;padding:24px 26px;font-size:15px\">{bodyHtml}</div>" +
               $"{footer}</div></body></html>";
    }
}

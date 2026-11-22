using System.Globalization;
using System.Text;

namespace Homepage.Common;

public static class Slug
{
    /// <summary>lower-case ASCII letters, digits and single dashes, at most 80 characters.</summary>
    public static string From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = text.Trim().ToLowerInvariant().Replace("ł", "l").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        var dash = false;
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(c); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            if (sb.Length >= 80) break;
        }
        return sb.ToString().Trim('-');
    }

    public static bool IsValid(string? slug) => !string.IsNullOrEmpty(slug) && slug == From(slug);
}

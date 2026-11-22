using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Homepage.Blocks;

/// <summary>
/// Font Awesome Free icons (solid, regular, brands), drawn as inline SVG: no web font and no stylesheet on public
/// pages. Names are "style:name", e.g. "solid:house" or "brands:github". Icons: CC BY 4.0 (Fonticons, Inc.).
/// </summary>
public static class Icons
{
    public sealed record Icon(string Key, int Width, int Height, string[] Paths);

    private static readonly Lazy<(Dictionary<string, Icon> Icons, Dictionary<string, string> Terms)> Data = new(Load);

    public static string Version => "7.3.1";
    public static int Count => Data.Value.Icons.Count;

    public static Icon? Get(string? key) => key is not null && Data.Value.Icons.TryGetValue(key, out var i) ? i : null;

    public static bool Exists(string? key) => Get(key) is not null;

    /// <summary>Icons whose name or search terms contain every word of the query, best matches first.</summary>
    public static IReadOnlyList<Icon> Search(string? query, int limit = 120)
    {
        var (icons, terms) = Data.Value;
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<Icon> all = icons.Values;
        if (words.Length == 0) return all.Where(i => i.Key.StartsWith("solid:", StringComparison.Ordinal)).Take(limit).ToList();
        return all
            .Select(i =>
            {
                var name = i.Key[(i.Key.IndexOf(':') + 1)..];
                var hay = name + " " + terms.GetValueOrDefault(name, "");
                if (!words.All(w => hay.Contains(w, StringComparison.Ordinal))) return (i, -1);
                var score = name == words[0] ? 0 : name.StartsWith(words[0], StringComparison.Ordinal) ? 1 : name.Contains(words[0], StringComparison.Ordinal) ? 2 : 3;
                return (i, score);
            })
            .Where(x => x.Item2 >= 0)
            .OrderBy(x => x.Item2).ThenBy(x => x.i.Key.StartsWith("solid:", StringComparison.Ordinal) ? 0 : 1).ThenBy(x => x.i.Key, StringComparer.Ordinal)
            .Take(limit).Select(x => x.i).ToList();
    }

    /// <summary>Inline SVG markup (fill = currentColor). The path data comes from the bundled set and is checked on load.</summary>
    public static string Svg(Icon icon, string cssClass = "icon")
    {
        var sb = new StringBuilder();
        sb.Append("<svg class=\"").Append(cssClass).Append("\" viewBox=\"0 0 ").Append(icon.Width).Append(' ').Append(icon.Height)
          .Append("\" aria-hidden=\"true\" focusable=\"false\" fill=\"currentColor\">");
        foreach (var d in icon.Paths) sb.Append("<path d=\"").Append(d).Append("\"/>");
        return sb.Append("</svg>").ToString();
    }

    private static (Dictionary<string, Icon>, Dictionary<string, string>) Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Homepage.Blocks.Icons.fontawesome-free.json")
                           ?? throw new InvalidOperationException("The icon set is missing from the build.");
        using var doc = JsonDocument.Parse(stream);
        var icons = new Dictionary<string, Icon>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.GetProperty("icons").EnumerateObject())
        {
            var a = p.Value;
            var paths = a[2].EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            // Path data is only numbers, letters, spaces, commas, dots and minus signs (no quotes or markup).
            if (paths.Any(d => d.Length == 0 || d.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or ',' or '.' or '-')))) continue;
            icons[p.Name] = new Icon(p.Name, int.Parse(a[0].GetString()!), int.Parse(a[1].GetString()!), paths);
        }
        var terms = doc.RootElement.GetProperty("terms").EnumerateObject().ToDictionary(t => t.Name, t => t.Value.GetString() ?? "", StringComparer.Ordinal);
        return (icons, terms);
    }
}

/// <summary>A Font Awesome icon name ("solid:house"), rendered as an icon picker; empty = none.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IconPickerAttribute : System.ComponentModel.DataAnnotations.ValidationAttribute
{
    public override bool IsValid(object? value) => value is not string s || s.Length == 0 || Icons.Exists(s);
    public override string FormatErrorMessage(string name) => $"{name}: unknown icon.";
}

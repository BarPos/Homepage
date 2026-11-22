using System.Text;

namespace Homepage.Web.Rendering;

/// <summary>Builds list URLs that keep every filter in the query string, so views can be bookmarked and shared.</summary>
public sealed class ListUrl(string path, IReadOnlyDictionary<string, string?> values)
{
    public string Path { get; } = path;
    public IReadOnlyDictionary<string, string?> Values { get; } = values;

    public string? this[string key] => Values.TryGetValue(key, out var v) ? v : null;

    public string With(string key, string? value)
    {
        var copy = new Dictionary<string, string?>(Values, StringComparer.Ordinal) { [key] = value };
        if (key != "page") copy.Remove("page");
        return Build(Path, copy);
    }

    public string Page(int page) => Build(Path, new Dictionary<string, string?>(Values, StringComparer.Ordinal) { ["page"] = page.ToString() });

    public static string Build(string path, IReadOnlyDictionary<string, string?> values)
    {
        var sb = new StringBuilder(path);
        var first = true;
        foreach (var (k, v) in values)
        {
            if (string.IsNullOrEmpty(v) || (k == "page" && v == "1")) continue;
            sb.Append(first ? '?' : '&').Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            first = false;
        }
        return sb.ToString();
    }

    public static ListUrl From(HttpRequest req, params string[] keys) =>
        new(req.Path, keys.ToDictionary(k => k, k => (string?)req.Query[k].ToString(), StringComparer.Ordinal));
}

using System.Globalization;
using Homepage.Analytics;

namespace Homepage.Web.Rendering;

/// <summary>Numbers, durations, names and links for the analytics page.</summary>
public static class AnalyticsFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Number(double n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000).ToString(n >= 10_000_000 ? "0" : "0.#", Inv) + "M",
        >= 10_000 => (n / 1000).ToString("0", Inv) + "k",
        >= 1000 => (n / 1000).ToString("0.#", Inv) + "k",
        _ => n.ToString("0", Inv),
    };

    public static string Duration(double seconds)
    {
        var s = (int)Math.Round(seconds);
        if (s < 60) return s + "s";
        if (s < 3600) return $"{s / 60}m {s % 60}s";
        return $"{s / 3600}h {s % 3600 / 60}m";
    }

    public static string Percent(double fraction) => (fraction * 100).ToString("0", Inv) + "%";

    /// <summary>Change against the previous period, or null when there is nothing to compare with.</summary>
    public static double? Change(double now, double before) => before <= 0 ? (now > 0 ? null : 0) : (now - before) / before;

    public static string CountryName(string? code)
    {
        if (code is null) return "Unknown";
        try { return new RegionInfo(code).EnglishName; } catch (ArgumentException) { return code; }
    }

    /// <summary>The flag emoji of a country code (regional indicator letters).</summary>
    public static string Flag(string? code) =>
        code is { Length: 2 } && char.IsAsciiLetterUpper(code[0]) && char.IsAsciiLetterUpper(code[1])
            ? char.ConvertFromUtf32(0x1F1E6 + code[0] - 'A') + char.ConvertFromUtf32(0x1F1E6 + code[1] - 'A') : "🌐";

    public static string Label(string dimension, string? value) => (dimension, value) switch
    {
        ("country", _) => CountryName(value),
        ("source" or "referrer", null) => "Direct / none",
        (_, null) => "(none)",
        _ => value,
    };

    public static string BucketLabel(DateTime local, string unit) => unit switch
    {
        "hour" => local.ToString("HH:mm", Inv),
        "day" => local.ToString("d MMM", Inv),
        _ => local.ToString("MMM yyyy", Inv),
    };

    public static string BucketTitle(DateTime local, string unit) => unit switch
    {
        "hour" => local.ToString("ddd d MMM, HH:mm", Inv),
        "day" => local.ToString("ddd d MMM yyyy", Inv),
        _ => local.ToString("MMMM yyyy", Inv),
    };
}

/// <summary>The analytics page's URL state: range and filters. Builds links that change one thing.</summary>
public sealed class AnalyticsUrl
{
    public string RangeKey { get; init; } = "30d";
    public string? From { get; init; }
    public string? To { get; init; }
    public Dictionary<string, string> Filters { get; init; } = [];

    public static AnalyticsUrl Parse(HttpRequest req)
    {
        var filters = new Dictionary<string, string>();
        foreach (var key in AnalyticsQueries.Dimensions.Keys)
            if (req.Query[key].ToString() is { Length: > 0 and <= 300 } v) filters[key] = v;
        return new AnalyticsUrl
        {
            RangeKey = req.Query["range"].ToString() is { Length: > 0 and <= 20 } r ? r : "30d",
            From = req.Query["from"].ToString() is { Length: 10 } f ? f : null,
            To = req.Query["to"].ToString() is { Length: 10 } t ? t : null,
            Filters = filters,
        };
    }

    public string Link(string path = "/admin/analytics", string? range = null, string? addKey = null, string? addValue = null, string? removeKey = null,
        bool keepCustom = true)
    {
        var q = new List<string>();
        var r = range ?? RangeKey;
        q.Add("range=" + Uri.EscapeDataString(r));
        if (r == "custom" && keepCustom && From is not null && To is not null) { q.Add("from=" + From); q.Add("to=" + To); }
        var f = new Dictionary<string, string>(Filters);
        if (removeKey is not null) f.Remove(removeKey);
        if (addKey is not null && addValue is not null) f[addKey] = addValue;
        foreach (var (k, v) in f.OrderBy(x => x.Key)) q.Add(Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v));
        return path + "?" + string.Join("&", q);
    }
}

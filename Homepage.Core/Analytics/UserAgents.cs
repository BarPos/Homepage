using System.Text.RegularExpressions;

namespace Homepage.Analytics;

/// <summary>A small user-agent reader: browser, operating system, and whether it is a bot. No external database.</summary>
public static partial class UserAgents
{
    [GeneratedRegex(@"bot\b|bot/|crawl|spider|slurp|facebookexternalhit|embedly|preview|headless|lighthouse|pingdom|uptime|monitor|" +
                    @"curl/|wget/|python|java/|go-http|okhttp|httpclient|axios|node-fetch|undici|phantom|selenium|puppeteer|playwright|" +
                    @"bingpreview|mediapartners|feedfetcher|feedly|rss|scrapy|semrush|ahrefs|petalbot|bytespider|gptbot|claudebot|ccbot",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BotPattern();

    public static bool IsBot(string? userAgent) =>
        string.IsNullOrWhiteSpace(userAgent) || userAgent.Length < 20 || BotPattern().IsMatch(userAgent);

    public static (string Browser, string Os) Parse(string? ua)
    {
        ua ??= "";
        return (Browser(ua), Os(ua));
    }

    private static string Browser(string ua)
    {
        bool Has(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Edg/") || Has("EdgA/") || Has("EdgiOS/")) return "Edge";
        if (Has("OPR/") || Has("Opera")) return "Opera";
        if (Has("SamsungBrowser/")) return "Samsung Internet";
        if (Has("YaBrowser/")) return "Yandex";
        if (Has("Vivaldi/")) return "Vivaldi";
        if (Has("DuckDuckGo/") || Has("Ddg/")) return "DuckDuckGo";
        if (Has("Firefox/") || Has("FxiOS/")) return "Firefox";
        if (Has("CriOS/") || Has("Chrome/") || Has("Chromium/")) return "Chrome";
        if (Has("Safari/") && (Has("Version/") || Has("iPhone") || Has("iPad") || Has("Macintosh"))) return "Safari";
        return "Other";
    }

    private static string Os(string ua)
    {
        bool Has(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Windows NT") || Has("Windows Phone")) return "Windows";
        if (Has("iPhone") || Has("iPad") || Has("iPod")) return "iOS";
        if (Has("Android")) return "Android";
        if (Has("CrOS")) return "ChromeOS";
        if (Has("Mac OS X") || Has("Macintosh")) return "macOS";
        if (Has("Linux") || Has("X11")) return "Linux";
        return "Other";
    }

    /// <summary>Device class from the window width in CSS pixels (as the visitor sees the page).</summary>
    public static string Device(int width) => width switch
    {
        <= 0 => "Desktop",
        < 576 => "Mobile",
        < 992 => "Tablet",
        < 1440 => "Laptop",
        _ => "Desktop",
    };
}

namespace Homepage.Analytics;

/// <summary>Where a visit came from: channel and a readable source name.</summary>
public static class Referrers
{
    private static readonly (string Suffix, string Name, string Channel)[] Known =
    [
        // Mail first: mail.google.com must not match "google." (search).
        ("mail.google.com", "Gmail", Channels.Email), ("outlook.live.com", "Outlook", Channels.Email), ("outlook.office.com", "Outlook", Channels.Email),
        ("google.", "Google", Channels.Search), ("bing.com", "Bing", Channels.Search), ("duckduckgo.com", "DuckDuckGo", Channels.Search),
        ("search.yahoo.", "Yahoo", Channels.Search), ("yahoo.com", "Yahoo", Channels.Search), ("yandex.", "Yandex", Channels.Search),
        ("ecosia.org", "Ecosia", Channels.Search), ("baidu.com", "Baidu", Channels.Search), ("search.brave.com", "Brave Search", Channels.Search),
        ("startpage.com", "Startpage", Channels.Search), ("qwant.com", "Qwant", Channels.Search), ("kagi.com", "Kagi", Channels.Search),
        ("perplexity.ai", "Perplexity", Channels.Search), ("chatgpt.com", "ChatGPT", Channels.Referral),
        ("facebook.com", "Facebook", Channels.Social), ("fb.me", "Facebook", Channels.Social), ("instagram.com", "Instagram", Channels.Social),
        ("t.co", "X (Twitter)", Channels.Social), ("twitter.com", "X (Twitter)", Channels.Social), ("x.com", "X (Twitter)", Channels.Social),
        ("linkedin.com", "LinkedIn", Channels.Social), ("lnkd.in", "LinkedIn", Channels.Social), ("reddit.com", "Reddit", Channels.Social),
        ("news.ycombinator.com", "Hacker News", Channels.Social), ("youtube.com", "YouTube", Channels.Social), ("youtu.be", "YouTube", Channels.Social),
        ("pinterest.", "Pinterest", Channels.Social), ("tiktok.com", "TikTok", Channels.Social), ("threads.net", "Threads", Channels.Social),
        ("bsky.app", "Bluesky", Channels.Social), ("mastodon.social", "Mastodon", Channels.Social), ("discord.com", "Discord", Channels.Social),
        ("telegram.org", "Telegram", Channels.Social), ("t.me", "Telegram", Channels.Social), ("github.com", "GitHub", Channels.Referral),
    ];

    /// <summary>Lower-case host without "www.", or null for an empty, unparsable or non-http(s) referrer.</summary>
    public static string? Host(string? referrer)
    {
        if (string.IsNullOrWhiteSpace(referrer) || !Uri.TryCreate(referrer, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return referrer.StartsWith("android-app://", StringComparison.Ordinal) ? Clip(uri.Host) : null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host.Length == 0 ? null : Clip(host);
    }

    private static string Clip(string s) => s.Length <= 200 ? s : s[..200];

    private static bool Matches(string host, string suffix) =>
        suffix.EndsWith('.') ? host.StartsWith(suffix, StringComparison.Ordinal) || host.Contains("." + suffix, StringComparison.Ordinal)
                             : host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal);

    /// <summary>The source name and channel of a visit from its referring host (null = none) and UTM tags.</summary>
    public static (string? Source, string Channel) Classify(string? host, string? utmSource, string? utmMedium)
    {
        var medium = utmMedium?.ToLowerInvariant() ?? "";
        string? name = null, channel = null;
        if (host is not null)
            foreach (var (suffix, n, c) in Known)
                if (Matches(host, suffix)) { name = n; channel = c; break; }
        if (host is not null && (host.Contains("mastodon") || host.Contains("fosstodon"))) { name ??= "Mastodon"; channel ??= Channels.Social; }

        var source = !string.IsNullOrWhiteSpace(utmSource) ? utmSource : name ?? host;
        if (medium is "cpc" or "ppc" or "paid" or "paidsearch" or "paid_social" or "display" or "cpm") return (source, Channels.Paid);
        if (medium is "email" or "e-mail" or "newsletter") return (source, Channels.Email);
        if (medium is "social" or "social-network" or "social-media" or "sm") return (source, Channels.Social);
        if (channel is not null) return (source, channel);
        if (host is not null) return (source, Channels.Referral);
        return !string.IsNullOrWhiteSpace(utmSource) ? (source, Channels.Campaign) : (null, Channels.Direct);
    }
}

using Homepage.Data;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Analytics;

/// <summary>
/// One page view on the public site. No cookies and no personal data: <see cref="VisitorId"/> is a hash of the
/// visitor's address and browser with a salt that changes every day and is then deleted, so it cannot be traced back
/// to an address and does not link one day's visits to the next.
/// </summary>
public sealed class PageView
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public long VisitorId { get; set; }
    /// <summary>Page views of one visitor less than 30 minutes apart share a visit (session).</summary>
    public long SessionId { get; set; }
    /// <summary>The first page of its visit.</summary>
    public bool IsEntry { get; set; }
    /// <summary>Path without query string, e.g. /blog/hello-world.</summary>
    public string Path { get; set; } = "/";
    /// <summary>Host of the referring site (lower case, without www.), null for direct visits and internal links.</summary>
    public string? ReferrerHost { get; set; }
    /// <summary>Readable source: utm_source, a known name (Google, Reddit, …) or the referring host; null = direct.</summary>
    public string? Source { get; set; }
    /// <summary>Direct, Search, Social, Email, Paid, Referral or Campaign.</summary>
    public string Channel { get; set; } = Channels.Direct;
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    /// <summary>ISO 3166-1 alpha-2, null when unknown.</summary>
    public string? Country { get; set; }
    public string Browser { get; set; } = "Other";
    public string Os { get; set; } = "Other";
    /// <summary>Mobile, Tablet, Laptop or Desktop (from the window width).</summary>
    public string Device { get; set; } = "Desktop";
    /// <summary>Seconds the page was visible, reported when the visitor leaves it.</summary>
    public int EngagedSeconds { get; set; }
    /// <summary>Random id the script made for this page view, to attach the engagement time to it.</summary>
    public string PageKey { get; set; } = "";
}

/// <summary>The salt of one day (site time zone). Deleted the day after.</summary>
public sealed class AnalyticsSalt
{
    public DateOnly Day { get; set; }
    public byte[] Salt { get; set; } = [];
}

public static class Channels
{
    public const string Direct = "Direct", Search = "Search", Social = "Social", Email = "Email", Paid = "Paid",
        Referral = "Referral", Campaign = "Campaign";
}

/// <summary>Analytics settings (the settings table, keys analytics.*).</summary>
public sealed record AnalyticsSettings(bool Enabled = true, bool Countries = true, int RetentionDays = 730, string TimeZone = "UTC")
{
    public const string EnabledKey = "analytics.enabled";
    public const string CountriesKey = "analytics.countries";
    public const string RetentionKey = "analytics.retentionDays";
    public const string TimeZoneKey = "analytics.timeZone";
    public const int MinRetentionDays = 30, MaxRetentionDays = 3650;

    public static async Task<AnalyticsSettings> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var all = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("analytics.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var d = new AnalyticsSettings();
        return new AnalyticsSettings(
            all.TryGetValue(EnabledKey, out var e) ? e == "true" : d.Enabled,
            all.TryGetValue(CountriesKey, out var c) ? c == "true" : d.Countries,
            int.TryParse(all.GetValueOrDefault(RetentionKey), out var r) ? Math.Clamp(r, MinRetentionDays, MaxRetentionDays) : d.RetentionDays,
            all.GetValueOrDefault(TimeZoneKey) is { Length: > 0 } tz && TryTimeZone(tz) is not null ? tz : d.TimeZone);
    }

    /// <summary>The time zone for an IANA id (Europe/Warsaw, UTC, …), or null when unknown.</summary>
    public static TimeZoneInfo? TryTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    public TimeZoneInfo Zone => TryTimeZone(TimeZone) ?? TimeZoneInfo.Utc;
}

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Homepage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homepage.Analytics;

/// <summary>What the tracking script sent, plus the request's address and browser. Kept only in memory.</summary>
public sealed record RawHit(bool Engagement, DateTime AtUtc, IPAddress? Ip, string UserAgent, string Host, string Url,
    string? Referrer, int Width, string PageKey, int Seconds);

/// <summary>
/// Accepts hits from the public endpoint without waiting for the database: a bounded queue, written in batches by
/// <see cref="AnalyticsWriter"/>. When the queue is full (a flood), hits are dropped.
/// </summary>
public sealed class AnalyticsCollector(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    private readonly Channel<RawHit> _queue = Channel.CreateBounded<RawHit>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true,
    });
    private AnalyticsSettings? _settings;
    private DateTime _settingsAt;

    internal ChannelReader<RawHit> Reader => _queue.Reader;

    public bool TryEnqueue(RawHit hit) => _queue.Writer.TryWrite(hit);

    /// <summary>Settings, cached for 30 seconds (read on every public page render and hit).</summary>
    public async Task<AnalyticsSettings> SettingsAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_settings is { } s && now - _settingsAt < TimeSpan.FromSeconds(30)) return s;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        _settings = await AnalyticsSettings.LoadAsync(db, ct);
        _settingsAt = now;
        return _settings;
    }

    /// <summary>Synchronous read for rendering; the cached value (or defaults until the first load).</summary>
    public AnalyticsSettings CachedSettings
    {
        get
        {
            if (_settings is null || time.GetUtcNow().UtcDateTime - _settingsAt > TimeSpan.FromSeconds(30)) _ = SettingsAsync();
            return _settings ?? new AnalyticsSettings(Enabled: false);
        }
    }

    public void ForgetSettings() => _settings = null;
}

/// <summary>Turns hits into page views: visitor hash with the daily salt, visits, source, device, country. Also deletes old data.</summary>
internal sealed class AnalyticsWriter(AnalyticsCollector collector, IDbContextFactory<AppDbContext> dbFactory, CountryLookup countries,
    TimeProvider time, ILogger<AnalyticsWriter> logger) : BackgroundService
{
    private static readonly TimeSpan VisitGap = TimeSpan.FromMinutes(30);
    /// <summary>A visit in progress: its id, last page view, and where it came from (later pages inherit that).</summary>
    private sealed record Visit(long Session, DateTime Last, string? ReferrerHost, string? Source, string Channel, string? UtmSource,
        string? UtmMedium, string? UtmCampaign);

    private readonly ConcurrentDictionary<long, Visit> _visits = new();
    private (DateOnly Day, byte[] Salt)? _salt;
    private DateTime _lastCleanup = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var batch = new List<RawHit>(256);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Wake at least hourly, so old data is deleted on quiet sites too.
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    wait.CancelAfter(TimeSpan.FromHours(1));
                    try { if (!await collector.Reader.WaitToReadAsync(wait.Token)) return; }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                }
                while (batch.Count < 500 && collector.Reader.TryRead(out var hit)) batch.Add(hit);
                await WriteAsync(batch, stop);
                await CleanupIfDueAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Writing {Count} analytics hits failed", batch.Count); }
            finally { batch.Clear(); }
        }
    }

    internal async Task WriteAsync(IReadOnlyList<RawHit> hits, CancellationToken ct)
    {
        if (hits.Count == 0) return;
        var settings = await collector.SettingsAsync(ct);
        if (!settings.Enabled) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var hit in hits)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(hit.AtUtc, settings.Zone));
            var visitor = VisitorId(await SaltAsync(db, day, ct), hit);
            if (hit.Engagement)
            {
                var seconds = Math.Clamp(hit.Seconds, 0, 6 * 3600);
                // The page view may be in this batch, not saved yet.
                if (db.PageViews.Local.FirstOrDefault(p => p.VisitorId == visitor && p.PageKey == hit.PageKey) is { } pending)
                {
                    pending.EngagedSeconds = Math.Max(pending.EngagedSeconds, seconds);
                    continue;
                }
                var since = hit.AtUtc.AddHours(-12);
                await db.PageViews.Where(p => p.VisitorId == visitor && p.PageKey == hit.PageKey && p.AtUtc > since && p.EngagedSeconds < seconds)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.EngagedSeconds, seconds), ct);
                continue;
            }
            db.PageViews.Add(await ToPageViewAsync(db, hit, visitor, settings, ct));
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<PageView> ToPageViewAsync(AppDbContext db, RawHit hit, long visitor, AnalyticsSettings settings, CancellationToken ct)
    {
        // The visit: continue the visitor's last one when it is less than 30 minutes old.
        if (!_visits.TryGetValue(visitor, out var last))
        {
            var prev = await db.PageViews.AsNoTracking().Where(p => p.VisitorId == visitor).OrderByDescending(p => p.AtUtc)
                .Select(p => new Visit(p.SessionId, p.AtUtc, p.ReferrerHost, p.Source, p.Channel, p.UtmSource, p.UtmMedium, p.UtmCampaign))
                .FirstOrDefaultAsync(ct);
            last = prev;
        }
        var continues = last is not null && hit.AtUtc - last.Last < VisitGap;

        var (path, query) = SplitUrl(hit.Url);
        var host = Referrers.Host(hit.Referrer);
        if (host is not null && string.Equals(host, StripWww(hit.Host), StringComparison.OrdinalIgnoreCase)) host = null; // internal link
        string? utmSource = Utm(query, "utm_source") ?? Utm(query, "ref"), utmMedium = Utm(query, "utm_medium"), utmCampaign = Utm(query, "utm_campaign");
        Visit visit;
        if (continues && host is null && utmSource is null)
            visit = last! with { Last = hit.AtUtc }; // a page within the visit: it keeps the visit's source
        else
        {
            var (source, channel) = Referrers.Classify(host, utmSource, utmMedium);
            visit = new Visit(continues ? last!.Session : RandomId(), hit.AtUtc, host, Clip(source, 100), channel, utmSource, utmMedium, utmCampaign);
        }
        _visits[visitor] = visit;
        if (_visits.Count > 50_000) foreach (var old in _visits.Where(v => hit.AtUtc - v.Value.Last > VisitGap).Select(v => v.Key).ToList()) _visits.TryRemove(old, out _);

        var (browser, os) = UserAgents.Parse(hit.UserAgent);
        return new PageView
        {
            AtUtc = hit.AtUtc, VisitorId = visitor, SessionId = visit.Session, IsEntry = !continues, Path = path,
            ReferrerHost = visit.ReferrerHost, Source = visit.Source, Channel = visit.Channel,
            UtmSource = visit.UtmSource, UtmMedium = visit.UtmMedium, UtmCampaign = visit.UtmCampaign,
            Country = settings.Countries ? countries.Find(hit.Ip) : null,
            Browser = browser, Os = os, Device = UserAgents.Device(hit.Width), PageKey = hit.PageKey,
        };
    }

    private static string StripWww(string host)
    {
        var h = host.Split(':')[0].ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    /// <summary>Path (decoded, without query or fragment, at most 300 characters, no trailing slash) and the query string.</summary>
    internal static (string Path, string Query) SplitUrl(string url)
    {
        var q = url.IndexOf('?');
        var path = q < 0 ? url : url[..q];
        var query = q < 0 ? "" : url[(q + 1)..];
        var hash = path.IndexOf('#');
        if (hash >= 0) path = path[..hash];
        try { path = Uri.UnescapeDataString(path); } catch (UriFormatException) { }
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0 || path[0] != '/') path = "/" + path;
        return (Clip(path, 300)!, query);
    }

    private static string? Utm(string query, string name)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !part.AsSpan(0, eq).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            string value;
            try { value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')).Trim(); } catch (UriFormatException) { continue; }
            return value.Length == 0 ? null : Clip(value, 100);
        }
        return null;
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static long RandomId() => BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;

    /// <summary>SHA-256 of salt, address, browser and site; the first 8 bytes. Without the salt (deleted after a day) it cannot be reversed.</summary>
    internal static long VisitorId(byte[] salt, RawHit hit)
    {
        var data = Encoding.UTF8.GetBytes($"{hit.Ip}|{hit.UserAgent}|{StripWww(hit.Host)}");
        var hash = HMACSHA256.HashData(salt, data);
        return BitConverter.ToInt64(hash, 0) & long.MaxValue;
    }

    private async Task<byte[]> SaltAsync(AppDbContext db, DateOnly day, CancellationToken ct)
    {
        if (_salt is { } s && s.Day == day) return s.Salt;
        var row = await db.AnalyticsSalts.AsNoTracking().FirstOrDefaultAsync(x => x.Day == day, ct);
        if (row is null)
        {
            row = new AnalyticsSalt { Day = day, Salt = RandomNumberGenerator.GetBytes(32) };
            await using var write = await dbFactory.CreateDbContextAsync(ct);
            write.AnalyticsSalts.Add(row);
            try { await write.SaveChangesAsync(ct); }
            catch (DbUpdateException) { row = await db.AnalyticsSalts.AsNoTracking().FirstAsync(x => x.Day == day, ct); } // another instance made it
            // Yesterday's salt is kept for hits that arrive around midnight; older ones are deleted.
            var keep = day.AddDays(-1);
            await write.AnalyticsSalts.Where(x => x.Day < keep).ExecuteDeleteAsync(ct);
        }
        _salt = (day, row.Salt);
        return row.Salt;
    }

    private async Task CleanupIfDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _lastCleanup < TimeSpan.FromHours(6)) return;
        _lastCleanup = now;
        var settings = await collector.SettingsAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var before = now.AddDays(-settings.RetentionDays);
        var n = await db.PageViews.Where(p => p.AtUtc < before).ExecuteDeleteAsync(ct);
        if (n > 0) logger.LogInformation("Deleted {Count} page views older than {Days} days", n, settings.RetentionDays);
    }
}

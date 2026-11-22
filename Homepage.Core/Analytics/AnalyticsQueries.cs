using System.Data.Common;
using System.Globalization;
using Homepage.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Homepage.Analytics;

/// <summary>A period in the site's time zone, in UTC for the queries.</summary>
public sealed record AnalyticsRange(string Key, string Label, DateTime FromLocal, DateTime ToLocal, DateTime FromUtc, DateTime ToUtc, string Unit)
{
    public static readonly IReadOnlyList<(string Key, string Label)> Presets =
    [
        ("today", "Today"), ("yesterday", "Yesterday"), ("7d", "Last 7 days"), ("30d", "Last 30 days"),
        ("month", "This month"), ("lastmonth", "Last month"), ("12mo", "Last 12 months"),
    ];

    /// <summary>A preset, or custom from/to dates (yyyy-MM-dd, inclusive). Unknown keys fall back to the last 30 days.</summary>
    public static AnalyticsRange Resolve(string? key, string? from, string? to, TimeZoneInfo tz, DateTime nowUtc)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        DateTime f, t;
        string label;
        if (key == "custom" && DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cf)
            && DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ct) && ct >= cf
            && (ct - cf).TotalDays <= 3660)
        {
            (f, t) = (cf, ct.AddDays(1));
            label = $"{cf:d MMM yyyy} – {ct:d MMM yyyy}";
        }
        else
        {
            key = Presets.Any(p => p.Key == key) ? key! : "30d";
            (f, t) = key switch
            {
                "today" => (today, today.AddDays(1)),
                "yesterday" => (today.AddDays(-1), today),
                "7d" => (today.AddDays(-6), today.AddDays(1)),
                "month" => (monthStart, today.AddDays(1)),
                "lastmonth" => (monthStart.AddMonths(-1), monthStart),
                "12mo" => (new DateTime(today.Year, today.Month, 1).AddMonths(-11), today.AddDays(1)),
                _ => (today.AddDays(-29), today.AddDays(1)),
            };
            label = Presets.First(p => p.Key == key).Label;
        }
        var days = (t - f).TotalDays;
        var unit = days <= 2 ? "hour" : days <= 92 ? "day" : "month";
        return new AnalyticsRange(key ?? "30d", label, f, t, LocalToUtc(f, tz), LocalToUtc(t, tz), unit);
    }

    /// <summary>
    /// The period just before, for the change in %. While this period is still running (today, this month), the earlier
    /// one is cut to the same elapsed time, so half a day is compared with the same half of the day before.
    /// </summary>
    public AnalyticsRange Previous(TimeZoneInfo tz, DateTime nowUtc)
    {
        var len = ToLocal - FromLocal;
        var f = Key is "month" or "lastmonth" or "12mo" && Unit != "hour"
            ? FromLocal.AddMonths(-(int)Math.Max(1, Math.Round(len.TotalDays / 30.4)))
            : FromLocal - len;
        var t = FromLocal;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        if (nowLocal > FromLocal && nowLocal < ToLocal) t = f + (nowLocal - FromLocal);
        return this with { FromLocal = f, ToLocal = t, FromUtc = LocalToUtc(f, tz), ToUtc = LocalToUtc(t, tz) };
    }

    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30); // a daylight-saving gap at midnight
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}

public sealed record AnalyticsSummary(long Visitors, long Visits, long Pageviews, double BounceRate, double VisitSeconds)
{
    public double ViewsPerVisit => Visits == 0 ? 0 : (double)Pageviews / Visits;
}

public sealed record AnalyticsPoint(DateTime Local, long Visitors, long Pageviews);

public sealed record AnalyticsRow(string? Value, long Visitors, long Pageviews);

/// <summary>Reports over page_views, in SQL. Dimensions and filters come from a fixed list (never from input).</summary>
public sealed class AnalyticsQueries(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Filterable and breakdown dimensions: URL key → column.</summary>
    public static readonly IReadOnlyDictionary<string, string> Dimensions = new Dictionary<string, string>
    {
        ["page"] = "Path", ["entry"] = "Path", ["source"] = "Source", ["channel"] = "Channel", ["referrer"] = "ReferrerHost",
        ["utm_source"] = "UtmSource", ["utm_medium"] = "UtmMedium", ["utm_campaign"] = "UtmCampaign",
        ["country"] = "Country", ["device"] = "Device", ["browser"] = "Browser", ["os"] = "Os",
    };

    /// <summary>The value a filter uses for "none" (direct visits, unknown country).</summary>
    public const string None = "(none)";

    private sealed class Sql
    {
        public readonly List<NpgsqlParameter> Parameters = [];
        public string Add(object value) { var n = "p" + Parameters.Count; Parameters.Add(new NpgsqlParameter(n, value)); return "@" + n; }
    }

    private static string Where(Sql sql, AnalyticsRange range, IReadOnlyDictionary<string, string> filters)
    {
        var parts = new List<string> { $"\"AtUtc\" >= {sql.Add(range.FromUtc)}", $"\"AtUtc\" < {sql.Add(range.ToUtc)}" };
        foreach (var (key, value) in filters)
        {
            if (!Dimensions.TryGetValue(key, out var col)) continue;
            if (key == "entry")
            {
                parts.Add($"\"SessionId\" IN (SELECT \"SessionId\" FROM page_views WHERE \"IsEntry\" AND \"Path\" = {sql.Add(value)} " +
                          $"AND \"AtUtc\" >= {sql.Add(range.FromUtc.AddHours(-1))} AND \"AtUtc\" < {sql.Add(range.ToUtc)})");
                continue;
            }
            parts.Add(value == None ? $"\"{col}\" IS NULL" : $"\"{col}\" = {sql.Add(value)}");
        }
        return "WHERE " + string.Join(" AND ", parts);
    }

    private async Task<List<T>> RunAsync<T>(string text, Sql sql, Func<DbDataReader, T> read, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(text, conn);
        cmd.Parameters.AddRange(sql.Parameters.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(read(r));
        return list;
    }

    public async Task<AnalyticsSummary> SummaryAsync(AnalyticsRange range, IReadOnlyDictionary<string, string> filters, CancellationToken ct = default)
    {
        var sql = new Sql();
        var text = $"""
            WITH v AS (SELECT "VisitorId", "SessionId", "AtUtc", "EngagedSeconds" FROM page_views {Where(sql, range, filters)}),
            s AS (SELECT count(*) AS n, greatest(extract(epoch FROM max("AtUtc") - min("AtUtc")), sum("EngagedSeconds")) AS d FROM v GROUP BY "SessionId")
            SELECT (SELECT count(DISTINCT "VisitorId") FROM v), (SELECT count(*) FROM s), (SELECT count(*) FROM v),
                   coalesce((SELECT avg(CASE WHEN n = 1 THEN 1.0 ELSE 0 END) FROM s), 0)::float8,
                   coalesce((SELECT avg(d) FROM s), 0)::float8
            """;
        return (await RunAsync(text, sql, r => new AnalyticsSummary(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetDouble(3), r.GetDouble(4)), ct)).Single();
    }

    public async Task<List<AnalyticsPoint>> TimeseriesAsync(AnalyticsRange range, IReadOnlyDictionary<string, string> filters, string timeZone,
        CancellationToken ct = default)
    {
        var sql = new Sql();
        var unit = sql.Add(range.Unit);
        var tz = sql.Add(timeZone);
        var text = $"""
            SELECT date_trunc({unit}, "AtUtc" AT TIME ZONE {tz}) AS b, count(DISTINCT "VisitorId"), count(*)
            FROM page_views {Where(sql, range, filters)} GROUP BY 1 ORDER BY 1
            """;
        var found = (await RunAsync(text, sql, r => new AnalyticsPoint(r.GetDateTime(0), r.GetInt64(1), r.GetInt64(2)), ct))
            .ToDictionary(p => p.Local);
        var points = new List<AnalyticsPoint>();
        // Buckets start where date_trunc puts them: a month bucket on the 1st, a day bucket at midnight.
        var start = range.Unit switch
        {
            "hour" => new DateTime(range.FromLocal.Year, range.FromLocal.Month, range.FromLocal.Day, range.FromLocal.Hour, 0, 0),
            "day" => range.FromLocal.Date,
            _ => new DateTime(range.FromLocal.Year, range.FromLocal.Month, 1),
        };
        for (var b = start; b < range.ToLocal; b = range.Unit switch { "hour" => b.AddHours(1), "day" => b.AddDays(1), _ => b.AddMonths(1) })
            points.Add(found.TryGetValue(b, out var p) ? p : new AnalyticsPoint(b, 0, 0));
        return points;
    }

    public async Task<List<AnalyticsRow>> BreakdownAsync(string dimension, AnalyticsRange range, IReadOnlyDictionary<string, string> filters,
        int limit = 10, CancellationToken ct = default)
    {
        if (!Dimensions.TryGetValue(dimension, out var col)) throw new ArgumentOutOfRangeException(nameof(dimension));
        var sql = new Sql();
        var where = Where(sql, range, filters);
        if (dimension == "entry") where += " AND \"IsEntry\"";
        var text = $"""
            SELECT "{col}", count(DISTINCT "VisitorId") AS vis, count(*) AS pv FROM page_views {where}
            GROUP BY 1 ORDER BY 2 DESC, 3 DESC, 1 LIMIT {sql.Add(Math.Clamp(limit, 1, 1000))}
            """;
        return await RunAsync(text, sql, r => new AnalyticsRow(r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
    }

    /// <summary>Visitors in the last 5 minutes, and the pages they are on.</summary>
    public async Task<(long Visitors, List<AnalyticsRow> Pages)> RealtimeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var sql = new Sql();
        var since = sql.Add(nowUtc.AddMinutes(-5));
        var text = $"""
            SELECT "Path", count(DISTINCT "VisitorId"), count(*) FROM page_views WHERE "AtUtc" >= {since}
            GROUP BY 1 ORDER BY 2 DESC LIMIT 5
            """;
        var pages = await RunAsync(text, sql, r => new AnalyticsRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        var sql2 = new Sql();
        var total = (await RunAsync($"SELECT count(DISTINCT \"VisitorId\") FROM page_views WHERE \"AtUtc\" >= {sql2.Add(nowUtc.AddMinutes(-5))}",
            sql2, r => r.GetInt64(0), ct)).Single();
        return (total, pages);
    }

    public async Task<long> CountAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PageViews.LongCountAsync(ct);
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PageViews.ExecuteDeleteAsync(ct);
    }
}

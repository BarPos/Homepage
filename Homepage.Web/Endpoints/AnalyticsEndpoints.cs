using System.Text.Json;
using System.Text.RegularExpressions;
using Homepage.Analytics;

namespace Homepage.Web.Endpoints;

/// <summary>POST /hp/event: page views and engagement time from js/hp-analytics.js. Always answers 204.</summary>
public static partial class AnalyticsEndpoints
{
    private const int MaxBody = 4096;

    [GeneratedRegex("^[a-z0-9]{8,40}$")]
    private static partial Regex PageKey();

    public static void MapAnalyticsCollection(this WebApplication app)
    {
        app.MapPost("/hp/event", async (HttpContext http, AnalyticsCollector collector, TimeProvider time) =>
        {
            var req = http.Request;
            var ua = req.Headers.UserAgent.ToString();
            if (!(await collector.SettingsAsync(http.RequestAborted)).Enabled || UserAgents.IsBot(ua) || !SameSite(req)) return Results.NoContent();
            if (req.ContentLength is > MaxBody) return Results.NoContent();
            string body;
            using (var reader = new StreamReader(req.Body))
            {
                var buffer = new char[MaxBody + 1];
                var n = await reader.ReadBlockAsync(buffer, http.RequestAborted);
                if (n > MaxBody) return Results.NoContent();
                body = new string(buffer, 0, n);
            }
            if (Parse(body) is not { } e) return Results.NoContent();
            collector.TryEnqueue(new RawHit(e.Engagement, time.GetUtcNow().UtcDateTime, http.Connection.RemoteIpAddress, ua[..Math.Min(ua.Length, 500)],
                req.Host.Host, e.Url, e.Referrer, e.Width, e.Key, e.Seconds));
            return Results.NoContent();
        }).RequireRateLimiting("analytics").DisableAntiforgery();
    }

    /// <summary>Only from pages of this site: the Origin (or Referer) must be this host.</summary>
    private static bool SameSite(HttpRequest req)
    {
        var origin = req.Headers.Origin.ToString();
        if (origin.Length == 0) origin = req.Headers.Referer.ToString();
        if (origin.Length == 0 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Host, req.Host.Host, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record Event(bool Engagement, string Url, string? Referrer, int Width, string Key, int Seconds);

    internal static Event? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
            var type = Str("t");
            var url = Str("u");
            var key = Str("k");
            if (type is not ("v" or "e") || url is null || key is null || !PageKey().IsMatch(key)) return null;
            if (url.Length is 0 or > 1000 || url[0] != '/' || url.StartsWith("//", StringComparison.Ordinal)
                || url.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) || url.StartsWith("/hp/", StringComparison.OrdinalIgnoreCase)) return null;
            var referrer = Str("r");
            if (referrer is { Length: > 1000 }) referrer = referrer[..1000];
            return new Event(type == "e", url, referrer, Math.Clamp(Int("w"), 0, 10000), key, Math.Clamp(Int("s"), 0, 6 * 3600));
        }
        catch (JsonException) { return null; }
    }
}

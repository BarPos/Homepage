using System.IO.Compression;
using System.Net;
using Homepage.Data;
using Homepage.Options;
using MaxMind.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homepage.Analytics;

public sealed class AnalyticsOptions
{
    public const string Section = "Analytics";
    /// <summary>
    /// A country database in MaxMind DB format (GeoLite2-Country.mmdb, dbip-country-lite.mmdb, …) that you keep up to
    /// date yourself. When empty, Homepage downloads the free DB-IP country database to data/geo/ once a month (when
    /// country lookup is on in the analytics settings).
    /// </summary>
    public string GeoDatabase { get; set; } = "";
}

/// <summary>IP address → ISO country code, from a MaxMind DB file. Without a file every lookup returns null.</summary>
public sealed class CountryLookup(IOptions<PathsOptions> paths, IOptions<AnalyticsOptions> options, ILogger<CountryLookup> logger) : IDisposable
{
    private readonly object _gate = new();
    private Reader? _reader;
    private DateTime _loadedWrite;

    public string DownloadPath => Path.Combine(paths.Value.Data, "geo", "dbip-country-lite.mmdb");
    public bool UsesOwnFile => !string.IsNullOrWhiteSpace(options.Value.GeoDatabase);
    public string FilePath => UsesOwnFile ? options.Value.GeoDatabase : DownloadPath;

    /// <summary>When the database file was written, or null when there is none.</summary>
    public DateTime? DatabaseDateUtc => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null;

    public string? Find(IPAddress? ip)
    {
        if (ip is null || IPAddress.IsLoopback(ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var reader = Current();
        if (reader is null) return null;
        try
        {
            var data = reader.Find<Dictionary<string, object>>(ip);
            if (data is null) return null;
            foreach (var key in new[] { "country", "registered_country" })
                if (data.TryGetValue(key, out var c) && c is Dictionary<string, object> country
                    && country.TryGetValue("iso_code", out var code) && code is string s && s.Length == 2)
                    return s.ToUpperInvariant();
        }
        catch (Exception ex) when (ex is InvalidDatabaseException or ArgumentException) { logger.LogDebug(ex, "country lookup failed"); }
        return null;
    }

    private Reader? Current()
    {
        var path = FilePath;
        lock (_gate)
        {
            if (!File.Exists(path)) { _reader?.Dispose(); _reader = null; return null; }
            var write = File.GetLastWriteTimeUtc(path);
            if (_reader is not null && write == _loadedWrite) return _reader;
            try
            {
                var next = new Reader(path, FileAccessMode.Memory);
                _reader?.Dispose();
                _reader = next;
                _loadedWrite = write;
            }
            catch (Exception ex) when (ex is IOException or InvalidDatabaseException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Cannot read the country database {Path}", path);
            }
            return _reader;
        }
    }

    public void Dispose() { lock (_gate) { _reader?.Dispose(); _reader = null; } }
}

/// <summary>Keeps data/geo/dbip-country-lite.mmdb fresh (monthly) while country lookup is on. DB-IP Lite, CC BY 4.0.</summary>
internal sealed class GeoDatabaseUpdater(IServiceProvider services, CountryLookup lookup, IHttpClientFactory http, IHostEnvironment env,
    ILogger<GeoDatabaseUpdater> logger) : BackgroundService
{
    public const string HttpClientName = "geo";
    private const long MaxBytes = 100L * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (env.IsEnvironment("Testing") || lookup.UsesOwnFile) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await RunOnceAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Country database update failed; retrying later"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stop); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using (var scope = services.CreateAsyncScope())
        {
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbf.CreateDbContextAsync(ct);
            if ((await AnalyticsSettings.LoadAsync(db, ct)) is not { Enabled: true, Countries: true }) return;
        }
        if (lookup.DatabaseDateUtc is { } d && DateTime.UtcNow - d < TimeSpan.FromDays(32)) return;
        var now = DateTime.UtcNow;
        foreach (var month in new[] { now, now.AddMonths(-1) })
        {
            var url = $"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.mmdb.gz";
            if (await TryDownloadAsync(url, ct)) { logger.LogInformation("Country database updated from {Url}", url); return; }
        }
    }

    private async Task<bool> TryDownloadAsync(string url, CancellationToken ct)
    {
        using var res = await http.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) return false;
        var target = lookup.DownloadPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp";
        await using (var body = await res.Content.ReadAsStreamAsync(ct))
        await using (var gz = new GZipStream(body, CompressionMode.Decompress))
        await using (var file = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await gz.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > MaxBytes) throw new InvalidDataException("The country database is too large.");
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        try { using var check = new Reader(tmp); } // must open as a MaxMind DB before it replaces the old one
        catch { File.Delete(tmp); throw; }
        File.Move(tmp, target, overwrite: true);
        return true;
    }
}

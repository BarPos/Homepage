// Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
using System.Globalization;
using System.Text;
using System.Text.Json;
using Homepage.Accounts;
using Homepage.Common;
using Homepage.Data;
using Homepage.Options;
using Homepage.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homepage.Updates;

public sealed record UpdateStatus(bool Configured, string CurrentVersion, ReleaseInfo? Latest, bool UpdateAvailable,
    DateTime? LastCheckedUtc, string? LastError, UpdateRun? LastRun, bool SelfUpdateSupported, string? UnsupportedReason);

/// <summary>
/// Self-update: release checks, staging the package in data/updates/&lt;version&gt;/ and writing the
/// <c>request.conf</c> that the root-owned <c>update.sh</c> applies (via homepage-update.path), and recording the
/// <c>result.conf</c> it leaves behind. The server never needs root.
/// </summary>
public sealed class ServerUpdateService(
    IReleaseSource releases,
    IServerInstallation installation,
    IDbContextFactory<AppDbContext> dbFactory,
    IAuditLog audit,
    IOptions<PathsOptions> paths,
    TimeProvider time,
    ILogger<ServerUpdateService> logger)
{
    public const long MaxTarballBytes = 1024L * 1024 * 1024;
    internal const string LastCheckKey = "updates.lastCheck";
    internal static readonly TimeSpan StaleRunAfter = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new();
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private static readonly SemaphoreSlim CheckLock = new(1, 1);

    private string UpdatesDir => paths.Value.UpdatesDirectory;

    public async Task<UpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await ProcessResultFileAsync(ct);
        var last = await ReadLastCheckAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lastRun = await db.UpdateRuns.AsNoTracking().OrderByDescending(r => r.RequestedAtUtc).FirstOrDefaultAsync(ct);
        var configured = releases.IsConfigured;
        var latest = configured ? last?.Latest : null;
        return new UpdateStatus(configured, AppVersion.Current, latest, configured && CheckTargetVersion(latest?.Version) is null,
            last?.CheckedAtUtc, configured ? last?.Error : null, lastRun, installation.SelfUpdateSupported, installation.UnsupportedReason);
    }

    public async Task<UpdateStatus> CheckNowAsync(string actor, CancellationToken ct = default)
    {
        if (!releases.IsConfigured) return await GetStatusAsync(ct);
        await CheckLock.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            LastCheck record;
            try
            {
                var latest = await releases.GetLatestAsync(ct);
                record = new LastCheck(now, latest?.Info, latest is null ? "No release found." : null);
            }
            catch (Exception ex) when (ex is ReleaseSourceException or HttpRequestException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Release check failed");
                record = new LastCheck(now, (await ReadLastCheckAsync(ct))?.Latest, ex.Message);
            }
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await SiteSettings.SetAsync(db, LastCheckKey, JsonSerializer.Serialize(record, Json), ct);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            CheckLock.Release();
        }
        return await GetStatusAsync(ct);
    }

    public async Task<OperationResult> UpdateNowAsync(string actor, CancellationToken ct = default)
    {
        if (!releases.IsConfigured) return OperationResult.Fail("Updates are not configured.");
        if (!installation.SelfUpdateSupported)
            return OperationResult.Fail(installation.UnsupportedReason ?? "Self-update is not supported on this server.");
        var arch = installation.Architecture;
        if (arch is null) return OperationResult.Fail("Unsupported CPU architecture.");

        if (!await UpdateLock.WaitAsync(0, ct)) return OperationResult.Fail("An update is already being prepared.");
        Guid? runId = null;
        string? versionDir = null;
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var cutoff = time.GetUtcNow().UtcDateTime - StaleRunAfter;
                var busy = await db.UpdateRuns.AnyAsync(r =>
                    (r.State == UpdateState.Downloading || r.State == UpdateState.Staged || r.State == UpdateState.Applying)
                    && r.RequestedAtUtc > cutoff, ct);
                if (busy) return OperationResult.Fail("An update is already in progress.");
            }

            Release? release;
            try { release = await releases.GetLatestAsync(ct); }
            catch (ReleaseSourceException ex) { return OperationResult.Fail(ex.Message); }
            if (release is null) return OperationResult.Fail("No release found.");
            var version = release.Info.Version;
            if (CheckTargetVersion(version) is { } refusal) return OperationResult.Fail(refusal);

            var tarName = ReleaseAssetNames.ServerTarball(version, arch);
            var tarAsset = release.FindAsset(tarName);
            var sumsAsset = release.FindAsset(ReleaseAssetNames.Sums);
            if (tarAsset is null || sumsAsset is null)
                return OperationResult.Fail($"Release {release.Info.TagName} has no {tarName} or {ReleaseAssetNames.Sums}.");

            var run = new UpdateRun
            {
                Id = Guid.NewGuid(), FromVersion = AppVersion.Current, ToVersion = version, RequestedBy = actor,
                State = UpdateState.Downloading, RequestedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            runId = run.Id;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                db.UpdateRuns.Add(run);
                await db.SaveChangesAsync(ct);
            }
            await audit.WriteAsync(actor, "update.requested", "UpdateRun", run.Id.ToString(), $"{AppVersion.Current} -> {version}", ct: ct);

            string sumsText;
            await using (var s = await releases.OpenAssetAsync(sumsAsset, ct))
                sumsText = await FileHashing.ReadSmallTextAsync(s, Sha256Sums.MaxBytes, ct);
            if (!Sha256Sums.Parse(sumsText).TryGetValue(tarName, out var expected))
                return await FailRunAsync(run.Id, $"{ReleaseAssetNames.Sums} has no entry for {tarName}.", ct);

            versionDir = Path.Combine(UpdatesDir, version);
            FileHashing.TryDeleteDirectory(versionDir);
            Directory.CreateDirectory(versionDir);
            var partial = Path.Combine(versionDir, tarName + ".partial");
            (long Size, string Sha256, byte[] Head) copied;
            await using (var s = await releases.OpenAssetAsync(tarAsset, ct))
                copied = await FileHashing.CopyAndHashAsync(s, partial, MaxTarballBytes, ct);
            if (!string.Equals(copied.Sha256, expected, StringComparison.Ordinal))
            {
                FileHashing.TryDeleteDirectory(versionDir);
                return await FailRunAsync(run.Id, $"{tarName}: SHA-256 mismatch (expected {expected}, got {copied.Sha256}).", ct);
            }
            File.Move(partial, Path.Combine(versionDir, tarName), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(versionDir, ReleaseAssetNames.Sums), sumsText, ct);

            // Request file, atomically: the path unit fires as soon as request.conf exists.
            var request = UpdateRequestFile.BuildRequest(run.Id, version, AppVersion.Current, arch, tarName, copied.Sha256);
            var temp = Path.Combine(UpdatesDir, $".{UpdateRequestFile.RequestFileName}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temp, request, new UTF8Encoding(false), ct);
            File.Move(temp, Path.Combine(UpdatesDir, UpdateRequestFile.RequestFileName), overwrite: true);

            await SetRunStateAsync(run.Id, UpdateState.Staged, $"Staged {tarName}; waiting for update.sh.", finished: false, ct);
            logger.LogInformation("Server update {From} -> {To} staged by {Actor}", AppVersion.Current, version, actor);
            return OperationResult.Success;
        }
        catch (Exception ex) when (ex is ReleaseSourceException or HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Server update failed");
            if (versionDir is not null) FileHashing.TryDeleteDirectory(versionDir);
            if (runId is { } id) return await FailRunAsync(id, ex.Message, CancellationToken.None);
            return OperationResult.Fail(ex.Message);
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    internal static string? CheckTargetVersion(string? version)
    {
        if (!AppVersion.IsPlainTriplet(version)) return $"Release version '{version}' is not a plain x.y.z version.";
        if (AppVersion.Compare(version!, AppVersion.Current) <= 0)
            return $"Release {version} is not newer than the running version {AppVersion.Current}.";
        return null;
    }

    /// <summary>Reads result.conf left by update.sh and finishes the matching run.</summary>
    internal async Task ProcessResultFileAsync(CancellationToken ct)
    {
        var resultPath = Path.Combine(UpdatesDir, UpdateRequestFile.ResultFileName);
        var now = time.GetUtcNow().UtcDateTime;
        Dictionary<string, string>? result = null;
        try
        {
            var fi = new FileInfo(resultPath);
            if (fi.Exists && fi.LinkTarget is null && fi.Length < 64 * 1024)
                result = UpdateRequestFile.ParseKeyValues(await File.ReadAllTextAsync(resultPath, ct));
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Cannot read {Path}", resultPath);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (result is not null)
        {
            var state = result.GetValueOrDefault("state") switch
            {
                "Succeeded" => UpdateState.Succeeded,
                "RolledBack" => UpdateState.RolledBack,
                _ => UpdateState.Failed,
            };
            var version = result.GetValueOrDefault("version") ?? "";
            var from = result.GetValueOrDefault("from_version") ?? "";
            var message = Truncate(result.GetValueOrDefault("message"), 2000);
            var finished = now;
            if (result.GetValueOrDefault("finished_at") is { } f &&
                DateTime.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                finished = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            UpdateRun? run = null;
            if (Guid.TryParse(result.GetValueOrDefault("run_id"), out var runId))
                run = await db.UpdateRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null)
            {
                run = new UpdateRun
                {
                    Id = runId == Guid.Empty ? Guid.NewGuid() : runId, FromVersion = Truncate(from, 32) ?? "",
                    ToVersion = Truncate(version, 32) ?? "", RequestedBy = "update.sh", RequestedAtUtc = finished,
                };
                db.UpdateRuns.Add(run);
            }
            run.State = state;
            run.Message = message;
            run.FinishedAtUtc = finished;
            await db.SaveChangesAsync(ct);
            FileHashing.TryDelete(resultPath);
            if (AppVersion.IsPlainTriplet(run.ToVersion)) FileHashing.TryDeleteDirectory(Path.Combine(UpdatesDir, run.ToVersion));
            await audit.WriteAsync("update.sh", "update.finished", "UpdateRun", run.Id.ToString(),
                $"{run.FromVersion} -> {run.ToVersion}: {state}. {message}", ct: ct);
            return;
        }

        var open = await db.UpdateRuns
            .Where(r => r.State == UpdateState.Downloading || r.State == UpdateState.Staged || r.State == UpdateState.Applying)
            .ToListAsync(ct);
        var changed = false;
        foreach (var run in open)
        {
            if (run.ToVersion == AppVersion.Current)
            {
                run.State = UpdateState.Succeeded;
                run.Message ??= "Running the new version.";
                run.FinishedAtUtc = now;
                changed = true;
            }
            else if (run.State == UpdateState.Downloading && UpdateLock.CurrentCount > 0)
            {
                run.State = UpdateState.Failed;
                run.Message = "The download was interrupted.";
                run.FinishedAtUtc = now;
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private async Task<OperationResult> FailRunAsync(Guid runId, string message, CancellationToken ct)
    {
        await SetRunStateAsync(runId, UpdateState.Failed, message, finished: true, ct);
        return OperationResult.Fail(message);
    }

    private async Task SetRunStateAsync(Guid runId, UpdateState state, string message, bool finished, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.UpdateRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;
        run.State = state;
        run.Message = Truncate(message, 2000);
        if (finished) run.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    internal async Task<LastCheck?> ReadLastCheckAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.Settings.AsNoTracking().Where(s => s.Key == LastCheckKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LastCheck>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    internal sealed record LastCheck(DateTime CheckedAtUtc, ReleaseInfo? Latest, string? Error);
}

/// <summary>Checks for releases every Updates:CheckIntervalHours (only when configured) and picks up result.conf after a restart.</summary>
internal sealed class UpdatesBackgroundService(IServiceProvider services, IOptions<UpdatesOptions> options, ILogger<UpdatesBackgroundService> logger)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.CheckIntervalHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ServerUpdateService>();
                await svc.ProcessResultFileAsync(stoppingToken);
                if (options.Value.IsConfigured) await svc.CheckNowAsync("scheduler", stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Update check failed");
            }
            try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

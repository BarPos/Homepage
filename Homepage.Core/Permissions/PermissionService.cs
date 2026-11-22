using Homepage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Permissions;

public interface IPermissionService
{
    /// <summary>The effective permissions of a user (cached per user and permission version).</summary>
    Task<EffectivePermissions> GetAsync(int userId, CancellationToken ct = default);

    /// <summary>Call after any change to users, groups, roles or grants. Caches drop on the next request.</summary>
    Task BumpVersionAsync(AppDbContext db, CancellationToken ct = default);
}

/// <summary>
/// Resolves OTRS-style permissions: direct user ↔ group grants plus role ↔ group grants of the user's valid roles,
/// on valid groups only, for valid users only. The permission version lives in the settings table, so a change
/// made by another process (the CLI) is seen too.
/// </summary>
internal sealed class PermissionService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache) : IPermissionService
{
    public const string VersionKey = "permissions.version";

    public async Task<EffectivePermissions> GetAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var version = await db.Settings.AsNoTracking().Where(s => s.Key == VersionKey).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "0";
        var key = $"perm:{userId}:{version}";
        if (cache.TryGetValue(key, out EffectivePermissions? cached) && cached is not null) return cached;

        var result = await LoadAsync(db, userId, ct);
        cache.Set(key, result, TimeSpan.FromMinutes(10));
        return result;
    }

    internal static async Task<EffectivePermissions> LoadAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().Where(g => g.Valid).Select(g => new { g.Id, g.Name }).ToListAsync(ct);
        var names = groups.ToDictionary(g => g.Name, g => g.Id, StringComparer.Ordinal);
        var userValid = await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Valid, ct);
        if (!userValid) return new EffectivePermissions(userId, [], names);

        var validGroupIds = groups.Select(g => g.Id).ToHashSet();
        var direct = await db.UserGroups.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new { x.GroupId, x.Permission }).ToListAsync(ct);
        var viaRoles = await (
            from ur in db.UserRoles.AsNoTracking()
            join r in db.PermissionRoles.AsNoTracking() on ur.RoleId equals r.Id
            join rg in db.RoleGroups.AsNoTracking() on r.Id equals rg.RoleId
            where ur.UserId == userId && r.Valid
            select new { rg.GroupId, rg.Permission }).ToListAsync(ct);

        var grants = direct.Concat(viaRoles)
            .Where(g => validGroupIds.Contains(g.GroupId))
            .Select(g => PermNames.TryParse(g.Permission, out var p) ? new Grant(g.GroupId, p) : (Grant?)null)
            .Where(g => g is not null)
            .Select(g => g!.Value);
        return new EffectivePermissions(userId, grants, names);
    }

    public async Task BumpVersionAsync(AppDbContext db, CancellationToken ct = default)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == VersionKey, ct);
        if (setting is null) db.Settings.Add(new Setting { Key = VersionKey, Value = "1" });
        else setting.Value = (long.TryParse(setting.Value, out var v) ? v + 1 : 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await db.SaveChangesAsync(ct);
    }
}

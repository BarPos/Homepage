using System.Text.Json;
using Homepage.Blocks;
using Homepage.Common;
using Homepage.Data;
using Homepage.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Content;

public sealed record MenuLink(string Label, string Url);

public sealed class MenuService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache, IAuditLog audit)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<MenuLink>> GetLinksAsync(string key, CancellationToken ct = default)
    {
        if (cache.TryGetValue("menu:" + key, out IReadOnlyList<MenuLink>? links) && links is not null) return links;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.Menus.AsNoTracking().Where(m => m.Key == key).Select(m => m.Items).FirstOrDefaultAsync(ct);
        links = Parse(json);
        cache.Set("menu:" + key, links, TimeSpan.FromMinutes(5));
        return links;
    }

    public static IReadOnlyList<MenuLink> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<MenuLink>>(json, Json)?.Where(l => SafeUrlAttribute.IsSafe(l.Url)).ToList() ?? []; }
        catch (JsonException) { return []; }
    }

    public async Task<List<Menu>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Menus.AsNoTracking().OrderBy(m => m.Key).ToListAsync(ct);
    }

    public async Task SaveAsync(EffectivePermissions p, string key, IReadOnlyList<MenuLink> links, string actor, CancellationToken ct = default)
    {
        if (!p.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException("Menus need layout: rw.");
        var clean = links.Where(l => !string.IsNullOrWhiteSpace(l.Label)).Select(l =>
        {
            if (!SafeUrlAttribute.IsSafe(l.Url)) throw new BlockException($"'{l.Url}' is not an allowed link.");
            return new MenuLink(l.Label.Trim()[..Math.Min(l.Label.Trim().Length, 100)], l.Url.Trim());
        }).Take(50).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var menu = await db.Menus.FirstOrDefaultAsync(m => m.Key == key, ct) ?? throw new NotFoundException();
        menu.Items = JsonSerializer.Serialize(clean, Json);
        await db.SaveChangesAsync(ct);
        cache.Remove("menu:" + key);
        await audit.WriteAsync(actor, "menu.save", "Menu", key, $"{clean.Count} links", ct: ct);
    }
}

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

    public async Task<Menu> CreateAsync(EffectivePermissions p, string name, string key, string actor, CancellationToken ct = default)
    {
        if (!p.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException("Menus need layout: rw.");
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new BlockException("The menu name must be 1 to 100 characters.");
        key = Slug.From(string.IsNullOrWhiteSpace(key) ? name : key);
        if (key.Length is 0 or > 50) throw new BlockException("The menu key must be 1 to 50 letters, digits or dashes.");
        if (key == "new") throw new BlockException("'new' cannot be a menu key.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Menus.AnyAsync(m => m.Key == key, ct)) throw new ConflictException($"A menu '{key}' already exists.");
        var menu = new Menu { Key = key, Name = name, Items = "[]" };
        db.Menus.Add(menu);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "menu.create", "Menu", key, name, ct: ct);
        return menu;
    }

    /// <summary>Deletes a menu. Menu blocks that pointed at it render nothing until they get another menu.</summary>
    public async Task DeleteAsync(EffectivePermissions p, string key, string actor, CancellationToken ct = default)
    {
        if (!p.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException("Menus need layout: rw.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var menu = await db.Menus.FirstOrDefaultAsync(m => m.Key == key, ct) ?? throw new NotFoundException();
        db.Menus.Remove(menu);
        await db.SaveChangesAsync(ct);
        cache.Remove("menu:" + key);
        await audit.WriteAsync(actor, "menu.delete", "Menu", key, menu.Name, ct: ct);
    }

    public async Task SaveAsync(EffectivePermissions p, string key, IReadOnlyList<MenuLink> links, string actor, CancellationToken ct = default,
        string? name = null)
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
        if (name?.Trim() is { Length: > 0 and <= 100 } n) menu.Name = n;
        await db.SaveChangesAsync(ct);
        cache.Remove("menu:" + key);
        await audit.WriteAsync(actor, "menu.save", "Menu", key, $"{clean.Count} links", ct: ct);
    }
}

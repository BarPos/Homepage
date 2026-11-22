using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Themes;

public sealed record ActiveTheme(int Id, int ThemeVersion, string Name, ThemeTokens Tokens, Blocks.BlockTree Header, Blocks.BlockTree Footer,
    string ContentHash = "")
{
    /// <summary>Immutable URL: a new version or different content (e.g. after a database restore) is a new URL.</summary>
    public string CssUrl => $"/theme/{Id}/{ThemeVersion}.css?h={ContentHash}";
}

/// <summary>Themes: active theme lookup (cached), CSS, activation, duplication and token edits (themes: rw).</summary>
public sealed class ThemeService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache, IAuditLog audit, TimeProvider time)
{
    private const string ActiveKey = "theme:active";

    public async Task<ActiveTheme> GetActiveAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(ActiveKey, out ActiveTheme? cached) && cached is not null) return cached;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Themes.AsNoTracking().OrderByDescending(x => x.IsActive).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        var active = t is null
            ? new ActiveTheme(0, 0, "Default", ThemeTokens.Defaults, BuiltInThemes.DefaultHeader(), BuiltInThemes.DefaultFooter())
            : ToActive(t);
        cache.Set(ActiveKey, active, TimeSpan.FromMinutes(5));
        return active;
    }

    public static ActiveTheme ToActive(Theme t)
    {
        var tokens = ThemeTokens.Parse(t.Tokens);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tokens.ToJson() + "\n" + t.CustomCss)))[..10];
        return new(t.Id, t.ThemeVersion, t.Name, tokens, Blocks.BlockTree.Parse(t.HeaderBlocks), Blocks.BlockTree.Parse(t.FooterBlocks), hash);
    }

    public void Invalidate() => cache.Remove(ActiveKey);

    /// <summary>The CSS of a theme version. Null when the version is not the current one (old URLs then 404 → no stale cache).</summary>
    public async Task<string?> GetCssAsync(int id, int version, CancellationToken ct = default)
    {
        var key = $"theme:css:{id}:{version}";
        if (cache.TryGetValue(key, out string? css) && css is not null) return css;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Themes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ThemeVersion == version, ct);
        if (t is null) return null;
        css = ThemeTokens.Parse(t.Tokens).ToCss(t.CustomCss);
        cache.Set(key, css, TimeSpan.FromHours(1));
        return css;
    }

    public async Task<List<Theme>> ListAsync(EffectivePermissions p, CancellationToken ct = default)
    {
        if (!p.HasSystem(SystemGroups.Themes, Perm.Ro) && !p.HasSystem(SystemGroups.Layout, Perm.Ro)) throw new AccessDeniedException();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Themes.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Theme> GetAsync(EffectivePermissions p, int id, CancellationToken ct = default)
    {
        if (!p.HasSystem(SystemGroups.Themes, Perm.Ro) && !p.HasSystem(SystemGroups.Layout, Perm.Ro)) throw new AccessDeniedException();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException();
    }

    public async Task ActivateAsync(EffectivePermissions p, int id, string actor, CancellationToken ct = default)
    {
        RequireRw(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Themes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException();
        foreach (var t in await db.Themes.Where(t => t.IsActive && t.Id != id).ToListAsync(ct)) t.IsActive = false;
        target.IsActive = true;
        await db.SaveChangesAsync(ct);
        Invalidate();
        await audit.WriteAsync(actor, "theme.activate", "Theme", id.ToString(), target.Name, ct: ct);
    }

    public async Task<Theme> DuplicateAsync(EffectivePermissions p, int id, string actor, CancellationToken ct = default)
    {
        RequireRw(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var src = await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException();
        var name = src.Name + " (copy)";
        var slug = Slug.From(name);
        for (var i = 2; await db.Themes.AnyAsync(t => t.Slug == slug, ct); i++) slug = Slug.From(name) + "-" + i;
        var copy = new Theme
        {
            Name = name, Slug = slug, IsBuiltIn = false, Tokens = src.Tokens, CustomCss = src.CustomCss,
            HeaderBlocks = src.HeaderBlocks, FooterBlocks = src.FooterBlocks, UpdatedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        db.Themes.Add(copy);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "theme.duplicate", "Theme", copy.Id.ToString(), $"{src.Name} → {name}", ct: ct);
        return copy;
    }

    /// <summary>Saves tokens (validated and clamped) and custom CSS; returns whether the public site changed.</summary>
    public async Task<bool> SaveAsync(EffectivePermissions p, int id, string name, ThemeTokens tokens, string customCss, string actor,
        CancellationToken ct = default)
    {
        RequireRw(p);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var t = await db.Themes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (t.IsBuiltIn) throw new AccessDeniedException("Built-in themes are read-only. Duplicate the theme to change it.");
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new Blocks.BlockException("The theme name must be 1 to 100 characters.");
        t.Name = name;
        t.Tokens = tokens.ToJson();
        t.CustomCss = customCss.Length > 100_000 ? customCss[..100_000] : customCss;
        t.ThemeVersion++;
        t.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Someone else saved this theme. Reload and try again."); }
        if (t.IsActive) Invalidate();
        await audit.WriteAsync(actor, "theme.save", "Theme", id.ToString(), $"{name} v{t.ThemeVersion}", ct: ct);
        return t.IsActive;
    }

    private static void RequireRw(EffectivePermissions p)
    {
        if (!p.HasSystem(SystemGroups.Themes, Perm.Rw)) throw new AccessDeniedException("This needs themes: rw.");
    }
}

using Homepage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Web.Rendering;

/// <summary>Read-only queries for the public site: published items only.</summary>
public sealed class PublicContent(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
{
    public async Task<List<ContentItem>> LatestPostsAsync(int count, int skip = 0, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking()
            .Where(c => c.Type == ContentType.Post && c.Status == ContentStatus.Published)
            .OrderByDescending(c => c.PublishedAtUtc).ThenByDescending(c => c.Id)
            .Skip(skip).Take(count)
            .Select(c => new ContentItem { Id = c.Id, Slug = c.Slug, Title = c.Title, Excerpt = c.Excerpt, PublishedAtUtc = c.PublishedAtUtc, UpdatedAtUtc = c.UpdatedAtUtc })
            .ToListAsync(ct);
    }

    public async Task<ContentItem?> GetPublishedAsync(ContentType type, string slug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Type == type && c.Slug == slug && c.Status == ContentStatus.Published, ct);
    }

    public async Task<ContentItem?> GetPublishedByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Status == ContentStatus.Published, ct);
    }

    public async Task<List<ContentItem>> SearchAsync(string query, int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking()
            .Where(c => c.Status == ContentStatus.Published && c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("simple", query)))
            .OrderByDescending(c => c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("simple", query)))
            .Take(take)
            .Select(c => new ContentItem { Id = c.Id, Type = c.Type, Slug = c.Slug, Title = c.Title, Excerpt = c.Excerpt, PublishedAtUtc = c.PublishedAtUtc })
            .ToListAsync(ct);
    }

    public async Task<List<ContentItem>> AllPublishedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking().Where(c => c.Status == ContentStatus.Published)
            .OrderByDescending(c => c.PublishedAtUtc)
            .Select(c => new ContentItem { Id = c.Id, Type = c.Type, Slug = c.Slug, Title = c.Title, Excerpt = c.Excerpt, PublishedAtUtc = c.PublishedAtUtc, UpdatedAtUtc = c.UpdatedAtUtc })
            .ToListAsync(ct);
    }

    /// <summary>/media/{stored name}, or null when the item is gone.</summary>
    public async Task<string?> MediaUrlAsync(int id, CancellationToken ct = default)
    {
        var key = "media:url:" + id;
        if (cache.TryGetValue(key, out string? url)) return url;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Media.AsNoTracking().Where(m => m.Id == id).Select(m => m.StoredName).FirstOrDefaultAsync(ct);
        url = stored is null ? null : "/media/" + stored;
        cache.Set(key, url, TimeSpan.FromMinutes(10));
        return url;
    }
}

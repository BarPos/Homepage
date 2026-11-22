using Homepage.Content;
using Homepage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Web.Rendering;

/// <summary>A published page or post in a list, with its public URL.</summary>
public sealed record PostSummary(int Id, ContentType Type, string Title, string Slug, string Excerpt, DateTime? PublishedAtUtc,
    DateTime UpdatedAtUtc, int? BlogId, string Url, string? Author = null, bool NoIndex = false);

/// <summary>Read-only queries for the public site: published items only.</summary>
public sealed class PublicContent(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache, SiteTree tree)
{
    private async Task<List<PostSummary>> ToSummariesAsync(IQueryable<ContentItem> q, CancellationToken ct)
    {
        var map = await tree.GetAsync(ct);
        var rows = await q.Select(c => new { c.Id, c.Type, c.Title, c.Slug, c.Excerpt, c.PublishedAtUtc, c.UpdatedAtUtc, c.BlogId, c.AuthorId, c.ParentId,
            c.InheritTheme, c.ThemeId, c.Layout, c.NoIndex }).ToListAsync(ct);
        var authors = await AuthorNamesAsync(rows.Where(r => r.Type == ContentType.Post).Select(r => r.AuthorId), ct);
        return rows.Select(c => new PostSummary(c.Id, c.Type, c.Title, c.Slug, c.Excerpt, c.PublishedAtUtc, c.UpdatedAtUtc, c.BlogId,
            map.Url(c.Type, c.Id, c.Slug, c.BlogId), c.AuthorId is { } a ? authors.GetValueOrDefault(a) : null,
            map.EffectiveFor(c.Type, c.ParentId, c.BlogId, new PageOptionValues(c.InheritTheme, c.ThemeId, c.Layout, c.NoIndex)).NoIndex)).ToList();
    }

    /// <summary>Display names of the given users (cached briefly; names change rarely).</summary>
    public async Task<Dictionary<int, string>> AuthorNamesAsync(IEnumerable<int?> userIds, CancellationToken ct = default)
    {
        var ids = userIds.OfType<int>().Distinct().ToList();
        var result = new Dictionary<int, string>();
        var missing = new List<int>();
        foreach (var id in ids)
        {
            if (cache.TryGetValue("author:" + id, out string? n)) { if (n is not null) result[id] = n; }
            else missing.Add(id);
        }
        if (missing.Count == 0) return result;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var found = await db.Users.AsNoTracking().Where(u => missing.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.UserName }).ToListAsync(ct);
        foreach (var id in missing)
        {
            var u = found.FirstOrDefault(f => f.Id == id);
            var name = u is null ? null : string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName;
            cache.Set("author:" + id, name, TimeSpan.FromMinutes(5));
            if (name is not null) result[id] = name;
        }
        return result;
    }

    /// <summary>Latest published posts of one blog, or of all blogs when <paramref name="blogId"/> is null.</summary>
    public async Task<List<PostSummary>> LatestPostsAsync(int count, int skip = 0, int? blogId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.ContentItems.AsNoTracking().Where(c => c.Type == ContentType.Post && c.Status == ContentStatus.Published);
        if (blogId is { } b) q = q.Where(c => c.BlogId == b);
        return await ToSummariesAsync(q.OrderByDescending(c => c.PublishedAtUtc).ThenByDescending(c => c.Id).Skip(skip).Take(count), ct);
    }

    public async Task<ContentItem?> GetPublishedPageAsync(string slug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Type == ContentType.Page && c.Slug == slug && c.Status == ContentStatus.Published, ct);
    }

    public async Task<ContentItem?> GetPublishedPostAsync(int blogId, string slug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Type == ContentType.Post && c.BlogId == blogId && c.Slug == slug && c.Status == ContentStatus.Published, ct);
    }

    public async Task<ContentItem?> GetPublishedByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Status == ContentStatus.Published, ct);
    }

    public async Task<List<PostSummary>> SearchAsync(string query, int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ToSummariesAsync(db.ContentItems.AsNoTracking()
            .Where(c => c.Status == ContentStatus.Published && c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("simple", query)))
            .OrderByDescending(c => c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("simple", query)))
            .Take(take), ct);
    }

    public async Task<List<PostSummary>> AllPublishedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ToSummariesAsync(db.ContentItems.AsNoTracking().Where(c => c.Status == ContentStatus.Published)
            .OrderByDescending(c => c.PublishedAtUtc), ct);
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

    public void ForgetMedia(int id) => cache.Remove("media:url:" + id);

    /// <summary>Call after a user's display name changes.</summary>
    public void ForgetAuthor(int userId) => cache.Remove("author:" + userId);
}

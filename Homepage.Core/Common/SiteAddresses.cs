using Homepage.Data;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Common;

/// <summary>
/// Pages and blogs share addresses: under one parent page (or at the top level) a slug is either a page or a blog.
/// One check for both, on create, rename and move. Some top-level addresses belong to the site itself.
/// </summary>
public static class SiteAddresses
{
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        { "admin", "search", "media", "theme", "healthz", "feed-xml", "sitemap-xml", "robots-txt", "content", "hp" };

    /// <summary>Null when <paramref name="slug"/> is free under <paramref name="parentPageId"/> (null = top level), else the reason.</summary>
    public static async Task<string?> WhyTakenAsync(AppDbContext db, string slug, int? exceptPageId = null, int? exceptBlogId = null,
        CancellationToken ct = default, int? parentPageId = null)
    {
        var where = parentPageId is null ? $"/{slug}" : $"'{slug}' under that page";
        if (parentPageId is null && Reserved.Contains(slug)) return $"The address '/{slug}' is used by the site itself.";
        if (await db.ContentItems.AnyAsync(c => c.Type == ContentType.Page && c.ParentId == parentPageId && c.Slug == slug && c.Id != exceptPageId, ct))
            return $"The address {Quote(where)} is already used by a page.";
        if (await db.Blogs.AnyAsync(b => b.ParentPageId == parentPageId && b.Slug == slug && b.Id != exceptBlogId, ct))
            return $"The address {Quote(where)} is already used by a blog.";
        return null;
    }

    private static string Quote(string s) => s.StartsWith('/') ? $"'{s}'" : s;
}

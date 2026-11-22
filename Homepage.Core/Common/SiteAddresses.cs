using Homepage.Data;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Common;

/// <summary>
/// The first URL segment is shared by pages (/about) and blogs (/blog, /releases). One list of addresses that belong
/// to the site itself, and one check used for both pages and blogs, on create and on rename.
/// </summary>
public static class SiteAddresses
{
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        { "admin", "search", "media", "theme", "healthz", "feed-xml", "sitemap-xml", "robots-txt", "content" };

    /// <summary>Null when <paramref name="slug"/> is free for a page or blog, else the reason.</summary>
    public static async Task<string?> WhyTakenAsync(AppDbContext db, string slug, int? exceptPageId = null, int? exceptBlogId = null,
        CancellationToken ct = default)
    {
        if (Reserved.Contains(slug)) return $"The address '/{slug}' is used by the site itself.";
        if (await db.ContentItems.AnyAsync(c => c.Type == ContentType.Page && c.Slug == slug && c.Id != exceptPageId, ct))
            return $"The address '/{slug}' is already used by a page.";
        if (await db.Blogs.AnyAsync(b => b.Slug == slug && b.Id != exceptBlogId, ct))
            return $"The address '/{slug}' is already used by a blog.";
        return null;
    }
}

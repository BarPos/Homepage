using Homepage.Blocks;
using Homepage.Common;
using Homepage.Data;
using Homepage.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Content;

/// <summary>Blogs: lookup (cached) and administration (layout: rw).</summary>
public sealed class BlogService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache, IAuditLog audit, TimeProvider time)
{
    private const string CacheKey = "blogs:all";

    public async Task<IReadOnlyList<Blog>> AllAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<Blog>? list) && list is not null) return list;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        list = await db.Blogs.AsNoTracking().OrderBy(b => b.Id).ToListAsync(ct);
        cache.Set(CacheKey, list, TimeSpan.FromMinutes(10));
        return list;
    }

    public async Task<Blog?> BySlugAsync(string slug, CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(b => b.Slug == slug);

    /// <summary>The blog that used to live at <paramref name="slug"/> (for a 301 to its current address).</summary>
    public async Task<Blog?> ByPreviousSlugAsync(string slug, CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(b => b.PreviousSlugs.Contains(slug));

    public async Task<Blog?> ByIdAsync(int id, CancellationToken ct = default) => (await AllAsync(ct)).FirstOrDefault(b => b.Id == id);

    public async Task<Dictionary<int, string>> SlugsAsync(CancellationToken ct = default) =>
        (await AllAsync(ct)).ToDictionary(b => b.Id, b => b.Slug);

    public void Invalidate() => cache.Remove(CacheKey);

    public async Task<Blog> SaveAsync(EffectivePermissions p, int? id, string name, string slug, string description, int postsPerPage,
        string actor, CancellationToken ct = default)
    {
        if (!p.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException("Blogs need layout: rw.");
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new BlockException("The blog name must be 1 to 100 characters.");
        slug = Slug.From(slug);
        if (slug.Length == 0) throw new BlockException("The address must contain letters or digits.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        Blog blog;
        if (id is { } bid) blog = await db.Blogs.FirstOrDefaultAsync(b => b.Id == bid, ct) ?? throw new NotFoundException();
        else
        {
            blog = new Blog { CreatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.Blogs.Add(blog);
        }
        if (await SiteAddresses.WhyTakenAsync(db, slug, exceptBlogId: blog.Id == 0 ? null : blog.Id, ct: ct) is { } why)
            throw new ConflictException(why);
        if (blog.Id != 0 && blog.Slug != slug)
        {
            // Keep the old address working: requests to it are redirected (301) to the new one.
            blog.PreviousSlugs = blog.PreviousSlugs.Where(s => s != slug).Prepend(blog.Slug).Distinct().Take(10).ToList();
        }
        // An address can only redirect to one blog.
        foreach (var other in await db.Blogs.Where(b => b.Id != blog.Id && b.PreviousSlugs.Contains(slug)).ToListAsync(ct))
            other.PreviousSlugs = other.PreviousSlugs.Where(s => s != slug).ToList();
        blog.Name = name;
        blog.Slug = slug;
        blog.Description = description.Trim().Length > 500 ? description.Trim()[..500] : description.Trim();
        blog.PostsPerPage = Math.Clamp(postsPerPage, 1, 50);
        await db.SaveChangesAsync(ct);
        Invalidate();
        await audit.WriteAsync(actor, id is null ? "blog.create" : "blog.update", "Blog", blog.Id.ToString(), $"{name} /{slug}", ct: ct);
        return blog;
    }

    public async Task DeleteAsync(EffectivePermissions p, int id, string actor, CancellationToken ct = default)
    {
        if (!p.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException("Blogs need layout: rw.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var blog = await db.Blogs.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException();
        if (await db.ContentItems.AnyAsync(c => c.BlogId == id, ct))
            throw new ConflictException("The blog still has posts (perhaps in groups you cannot see). They must be moved or deleted first.");
        if (await db.Blogs.CountAsync(ct) == 1) throw new ConflictException("The last blog cannot be deleted.");
        db.Blogs.Remove(blog);
        await db.SaveChangesAsync(ct);
        Invalidate();
        await audit.WriteAsync(actor, "blog.delete", "Blog", id.ToString(), blog.Name, ct: ct);
    }
}

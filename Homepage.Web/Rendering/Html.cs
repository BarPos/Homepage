using Homepage.Content;
using Homepage.Common;
using Homepage.Data;
using Homepage.Themes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Web.Rendering;

public static class Html
{
    /// <summary>A Razor component rendered statically (no render mode, no circuit) as the HTTP response.</summary>
    public static RazorComponentResult<T> Render<T>(object? parameters = null, int status = 200) where T : IComponent =>
        new(parameters ?? new { }) { StatusCode = status };

    public static RazorComponentResult<T> Render<T>(IReadOnlyDictionary<string, object?> parameters, int status = 200) where T : IComponent =>
        new(parameters) { StatusCode = status };

    /// <summary>
    /// htmx partial request (not hx-boost navigation, which expects a full page). Only the exact value "true" counts:
    /// the output cache varies on the header's value, and an empty or odd value must get the same full page as no header,
    /// or an anonymous request could put a fragment in the cache for everyone.
    /// </summary>
    public static bool IsFragmentRequest(HttpRequest r) =>
        r.Headers["HX-Request"].ToString() == "true" && r.Headers["HX-Boosted"].ToString() != "true";
}

public sealed class RenderContextFactory(IDbContextFactory<AppDbContext> dbFactory, ThemeService themes, Homepage.Content.SiteTree tree)
{
    public async Task<RenderContext> CreateAsync(bool edit = false, string region = "main", ActiveTheme? theme = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new RenderContext
        {
            Site = await SiteSettings.LoadAsync(db, ct),
            Theme = theme ?? await themes.GetActiveAsync(ct),
            Edit = edit,
            EditRegion = region,
            Links = await tree.GetAsync(ct),
        };
    }

    /// <summary>The context of a page or post: theme, layout, CSS and indexing in effect (its own over the pages above it).</summary>
    public async Task<RenderContext> CreateForItemAsync(ContentItem item, bool edit = false, string region = "main", CancellationToken ct = default)
    {
        var map = await tree.GetAsync(ct);
        return await WithOptionsAsync(map.EffectiveFor(item.Type, item.ParentId, item.BlogId, PageOptionValues.Of(item)), edit, region, ct);
    }

    /// <summary>The context of a blog's post list: the options of the page the blog is under.</summary>
    public async Task<RenderContext> CreateForBlogAsync(int blogId, CancellationToken ct = default) =>
        await WithOptionsAsync((await tree.GetAsync(ct)).ForBlog(blogId), false, "main", ct);

    private async Task<RenderContext> WithOptionsAsync(PageOptions o, bool edit, string region, CancellationToken ct)
    {
        var theme = o.ThemeId is { } tid ? await themes.GetPublishedAsync(tid, ct) : null;
        var ctx = await CreateAsync(edit, region, theme, ct);
        return new RenderContext
        {
            Site = ctx.Site, Theme = ctx.Theme, Edit = ctx.Edit, EditRegion = ctx.EditRegion, Links = ctx.Links,
            Layout = o.Layout, PageCss = o.Css, NoIndex = o.NoIndex,
        };
    }
}

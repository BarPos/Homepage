using System.Globalization;
using System.Text;
using System.Xml;
using Homepage.Blocks;
using Homepage.Data;
using Homepage.Media;
using Homepage.Themes;
using Homepage.Web.Rendering;
using Homepage.Web.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;

namespace Homepage.Web.Endpoints;

/// <summary>The public site. Every page is output-cached under the "site" tag, varied by the htmx headers.</summary>
public static class SiteEndpoints
{
    public const string CacheTag = "site";
    public const string CachePolicy = "public";
    private const int PageSize = 10;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "admin", "blog", "search", "media", "theme", "feed.xml", "sitemap.xml", "robots.txt", "healthz", "_content" };

    public static void AddPublicCachePolicy(OutputCacheOptions o) =>
        o.AddPolicy(CachePolicy, b => b.Tag(CacheTag).SetVaryByHeader("HX-Request", "HX-Boosted").SetVaryByQuery("skip", "q")
            .Expire(TimeSpan.FromMinutes(30)));

    public static void MapSite(this WebApplication app)
    {
        var site = app.MapGroup("").CacheOutput(CachePolicy);

        site.MapGet("/", async (RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            if (ctx.Site.HomePageId is { } id && await content.GetPublishedByIdAsync(id, ct) is { } home)
                return (IResult)Html.Render<PageView>(new { Ctx = ctx, Item = home, Tree = BlockTree.Parse(home.Blocks), IsHome = true });
            var posts = await content.LatestPostsAsync(PageSize + 1, 0, ct);
            return Html.Render<BlogIndex>(new { Ctx = ctx, Posts = posts.Take(PageSize).ToList(), NextSkip = posts.Count > PageSize ? PageSize : (int?)null });
        });

        site.MapGet("/blog", async (int? skip, HttpRequest req, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var s = Math.Max(0, skip ?? 0);
            var posts = await content.LatestPostsAsync(PageSize + 1, s, ct);
            var page = posts.Take(PageSize).ToList();
            int? next = posts.Count > PageSize ? s + PageSize : null;
            if (Html.IsFragmentRequest(req))
            {
                // "Load more": previous posts are already on the page, so return them plus the next page.
                var all = s > 0 ? (await content.LatestPostsAsync(s, 0, ct)).Concat(page).ToList() : page;
                return (IResult)Html.Render<PostListFragment>(new { Posts = all, NextSkip = next });
            }
            return Html.Render<BlogIndex>(new { Ctx = await ctxf.CreateAsync(ct: ct), Posts = page, NextSkip = next });
        });

        site.MapGet("/blog/{slug}", async (string slug, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var post = await content.GetPublishedAsync(ContentType.Post, slug, ct);
            return post is null
                ? (IResult)Html.Render<MessagePage>(new { Ctx = ctx }, 404)
                : Html.Render<PostView>(new { Ctx = ctx, Item = post, Tree = BlockTree.Parse(post.Blocks) });
        });

        site.MapGet("/search", async (string? q, HttpRequest req, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var query = (q ?? "").Trim();
            var results = query.Length is 0 or > 200 ? await content.LatestPostsAsync(PageSize, 0, ct) : await content.SearchAsync(query, 30, ct);
            if (Html.IsFragmentRequest(req)) return (IResult)Html.Render<PostListFragment>(new { Posts = results });
            return Html.Render<BlogIndex>(new { Ctx = await ctxf.CreateAsync(ct: ct), Posts = results });
        });

        site.MapGet("/feed.xml", async (HttpRequest req, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var baseUrl = BaseUrl(ctx.Site.BaseUrl, req);
            var posts = await content.LatestPostsAsync(20, 0, ct);
            var sb = new StringBuilder();
            using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            {
                w.WriteStartElement("rss");
                w.WriteAttributeString("version", "2.0");
                w.WriteStartElement("channel");
                w.WriteElementString("title", ctx.Site.Title);
                w.WriteElementString("link", baseUrl + "/");
                w.WriteElementString("description", ctx.Site.Tagline);
                foreach (var p in posts)
                {
                    w.WriteStartElement("item");
                    w.WriteElementString("title", p.Title);
                    w.WriteElementString("link", $"{baseUrl}/blog/{p.Slug}");
                    w.WriteElementString("guid", $"{baseUrl}/blog/{p.Slug}");
                    if (p.PublishedAtUtc is { } d) w.WriteElementString("pubDate", d.ToString("r", CultureInfo.InvariantCulture));
                    w.WriteElementString("description", p.Excerpt);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
            }
            return Results.Text(sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\""), "application/rss+xml; charset=utf-8");
        });

        site.MapGet("/sitemap.xml", async (HttpRequest req, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var baseUrl = BaseUrl(ctx.Site.BaseUrl, req);
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            sb.Append("<url><loc>").Append(System.Security.SecurityElement.Escape(baseUrl + "/")).Append("</loc></url>\n");
            foreach (var c in await content.AllPublishedAsync(ct))
            {
                if (c.Id == ctx.Site.HomePageId) continue;
                var path = c.Type == ContentType.Post ? "/blog/" + c.Slug : "/" + c.Slug;
                sb.Append("<url><loc>").Append(System.Security.SecurityElement.Escape(baseUrl + path)).Append("</loc><lastmod>")
                  .Append(c.UpdatedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</lastmod></url>\n");
            }
            sb.Append("</urlset>\n");
            return Results.Text(sb.ToString(), "application/xml; charset=utf-8");
        });

        site.MapGet("/robots.txt", async (HttpRequest req, RenderContextFactory ctxf, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            return Results.Text($"User-agent: *\nDisallow: /admin/\nSitemap: {BaseUrl(ctx.Site.BaseUrl, req)}/sitemap.xml\n", "text/plain");
        });

        // Not output-cached: files on disk, served with their own long cache headers.
        app.MapGet("/media/{name}", (string name, MediaService media, HttpResponse res) =>
        {
            var path = media.ResolveFile(name);
            if (path is null) return Results.NotFound();
            var sniff = File.OpenRead(path);
            var head = new byte[16];
            var n = sniff.Read(head, 0, head.Length);
            sniff.Dispose();
            var type = MediaService.Sniff(head.AsSpan(0, n))?.Type ?? "application/octet-stream";
            res.Headers.CacheControl = "public, max-age=31536000, immutable";
            res.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return Results.File(path, type, enableRangeProcessing: true);
        });

        app.MapGet("/theme/{id:int}/{file}", async (int id, string file, ThemeService themes, HttpResponse res, CancellationToken ct) =>
        {
            if (!file.EndsWith(".css", StringComparison.Ordinal) || !int.TryParse(file[..^4], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                return Results.NotFound();
            var css = await themes.GetCssAsync(id, version, ct);
            if (css is null) return Results.NotFound();
            res.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Text(css, "text/css; charset=utf-8");
        });

        site.MapGet("/{slug}", async (string slug, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var page = Reserved.Contains(slug) ? null : await content.GetPublishedAsync(ContentType.Page, slug, ct);
            return page is null
                ? (IResult)Html.Render<MessagePage>(new { Ctx = ctx }, 404)
                : Html.Render<PageView>(new { Ctx = ctx, Item = page, Tree = BlockTree.Parse(page.Blocks), IsHome = page.Id == ctx.Site.HomePageId });
        });
    }

    private static string BaseUrl(string configured, HttpRequest req) =>
        !string.IsNullOrWhiteSpace(configured) ? configured.TrimEnd('/') : $"{req.Scheme}://{req.Host}";
}

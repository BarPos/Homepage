using System.Globalization;
using System.Text;
using System.Xml;
using Homepage.Blocks;
using Homepage.Content;
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

    public static void AddPublicCachePolicy(OutputCacheOptions o) =>
        // Vary by whether this is a fragment request (the same test as Html.IsFragmentRequest), not by raw header text,
        // so odd header values cannot create extra cache entries or a fragment cached as a full page.
        o.AddPolicy(CachePolicy, b => b.Tag(CacheTag).VaryByValue(ctx => new KeyValuePair<string, string>("fragment", Html.IsFragmentRequest(ctx.Request) ? "1" : "0"))
            .SetVaryByQuery("skip", "q")
            .Expire(TimeSpan.FromMinutes(30)));

    public static void MapSite(this WebApplication app)
    {
        var site = app.MapGroup("").CacheOutput(CachePolicy);

        site.MapGet("/", async (int? skip, HttpRequest req, RenderContextFactory ctxf, PublicContent content, BlogService blogs, SiteTree tree,
            CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            if (ctx.Site.HomePageId is { } id && await content.GetPublishedByIdAsync(id, ct) is { Type: ContentType.Page } home)
                return (IResult)Html.Render<PageView>(new { Ctx = await ctxf.CreateForItemAsync(home, ct: ct), Item = home, Tree = BlockTree.Parse(home.Blocks), IsHome = true });
            var blog = (ctx.Site.HomeBlogId is { } bid ? await blogs.ByIdAsync(bid, ct) : null) ?? (await blogs.AllAsync(ct)).FirstOrDefault();
            if (blog is null) return Html.Render<MessagePage>(new { Ctx = ctx, Heading = "Nothing here yet", Text = "Sign in at /admin to add a page." });
            return await BlogPage(blog, "/", (await tree.GetAsync(ct)).BlogPath(blog.Id), skip, req, await ctxf.CreateForBlogAsync(blog.Id, ct), content, ct);
        });

        // Everything else: pages (/about, /about/team), blogs (/blog, /docs/releases), posts (…/{post}), feeds (…/feed.xml),
        // and old blog addresses (301 to the new one). Literal routes (search, feeds, media, …) take precedence.
        site.MapGet("/{**path}", async (string path, int? skip, HttpRequest req, RenderContextFactory ctxf, PublicContent content, BlogService blogs,
            SiteTree tree, CancellationToken ct) =>
        {
            var map = await tree.GetAsync(ct);
            switch (map.Resolve(path))
            {
                case SitePath.Page(var id) when await content.GetPublishedByIdAsync(id, ct) is { Type: ContentType.Page } page:
                {
                    var ctx = await ctxf.CreateForItemAsync(page, ct: ct);
                    return (IResult)Html.Render<PageView>(new { Ctx = ctx, Item = page, Tree = BlockTree.Parse(page.Blocks), IsHome = page.Id == ctx.Site.HomePageId, Path = map.PagePath(page.Id) });
                }
                case SitePath.BlogIndex(var blogId, var blogPath) when await blogs.ByIdAsync(blogId, ct) is { } blog:
                    return await BlogPage(blog, blogPath, blogPath, skip, req, await ctxf.CreateForBlogAsync(blog.Id, ct), content, ct);
                case SitePath.Feed(var blogId, var blogPath) when await blogs.ByIdAsync(blogId, ct) is { } blog:
                    return Feed(await ctxf.CreateAsync(ct: ct), req, blog.Name, blog.Description, blogPath, await content.LatestPostsAsync(20, 0, blog.Id, ct));
                case SitePath.Post(var blogId, var blogPath, var slug) when await blogs.ByIdAsync(blogId, ct) is { } blog
                                                                             && await content.GetPublishedPostAsync(blog.Id, slug, ct) is { } post:
                    return Html.Render<PostView>(new { Ctx = await ctxf.CreateForItemAsync(post, ct: ct), Item = post, Tree = BlockTree.Parse(post.Blocks), Blog = blog,
                        BlogPath = blogPath, Author = (await content.AuthorNamesAsync([post.AuthorId], ct)).GetValueOrDefault(post.AuthorId ?? 0) });
                case SitePath.Moved(var to):
                    return Results.Redirect(to + req.QueryString, permanent: true);
                default:
                    return Html.Render<MessagePage>(new { Ctx = await ctxf.CreateAsync(ct: ct) }, 404);
            }
        });

        site.MapGet("/search", async (string? q, HttpRequest req, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var query = (q ?? "").Trim();
            var results = query.Length is 0 or > 200 ? await content.LatestPostsAsync(PageSize, 0, null, ct) : await content.SearchAsync(query, 30, ct);
            if (Html.IsFragmentRequest(req)) return (IResult)Html.Render<PostListFragment>(new { Posts = results });
            return Html.Render<BlogIndex>(new { Ctx = await ctxf.CreateAsync(ct: ct), Posts = results, Heading = "Search", Query = query });
        });

        site.MapGet("/feed.xml", async (HttpRequest req, RenderContextFactory ctxf, PublicContent content, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            return Feed(ctx, req, ctx.Site.Title, ctx.Site.Tagline, "/", await content.LatestPostsAsync(20, 0, null, ct));
        });

        site.MapGet("/sitemap.xml", async (HttpRequest req, RenderContextFactory ctxf, PublicContent content, BlogService blogs, SiteTree tree, CancellationToken ct) =>
        {
            var ctx = await ctxf.CreateAsync(ct: ct);
            var baseUrl = BaseUrl(ctx.Site.BaseUrl, req);
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            sb.Append("<url><loc>").Append(System.Security.SecurityElement.Escape(baseUrl + "/")).Append("</loc></url>\n");
            var map = await tree.GetAsync(ct);
            foreach (var b in await blogs.AllAsync(ct))
                sb.Append("<url><loc>").Append(System.Security.SecurityElement.Escape(baseUrl + map.BlogPath(b.Id))).Append("</loc></url>\n");
            foreach (var c in await content.AllPublishedAsync(ct))
            {
                if (c.Id == ctx.Site.HomePageId || c.NoIndex) continue;
                sb.Append("<url><loc>").Append(System.Security.SecurityElement.Escape(baseUrl + c.Url)).Append("</loc><lastmod>")
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

        // Browsers ask for /favicon.ico by themselves: send them to the ICO (or PNG) chosen in the settings.
        app.MapGet("/favicon.ico", async (RenderContextFactory ctxf, PublicContent content, HttpResponse res, CancellationToken ct) =>
        {
            var site = (await ctxf.CreateAsync(ct: ct)).Site;
            foreach (var id in new[] { site.FaviconIcoMediaId, site.FaviconMediaId, site.FaviconSvgMediaId })
                if (id is { } mid && await content.MediaUrlAsync(mid, ct) is { } url)
                {
                    res.Headers.CacheControl = "public, max-age=86400";
                    return Results.Redirect(url);
                }
            return Results.NotFound();
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
            var type = MediaService.Sniff(head.AsSpan(0, n))?.Type ?? (name.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : "application/octet-stream");
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

    }

    /// <param name="path">Where this list is (/ for the home page), for "load more" links.</param>
    /// <param name="blogPath">The blog's own address, for its feed.</param>
    private static async Task<IResult> BlogPage(Blog blog, string path, string blogPath, int? skip, HttpRequest req, RenderContext ctx, PublicContent content,
        CancellationToken ct)
    {
        var size = Math.Clamp(blog.PostsPerPage, 1, 50);
        var s = Math.Max(0, skip ?? 0);
        var posts = await content.LatestPostsAsync(size + 1, s, blog.Id, ct);
        var page = posts.Take(size).ToList();
        var next = posts.Count > size ? $"{path}?skip={s + size}" : null;
        if (Html.IsFragmentRequest(req))
        {
            // "Load more": the earlier posts are on the page already, so return them plus the next page.
            var all = s > 0 ? (await content.LatestPostsAsync(s, 0, blog.Id, ct)).Concat(page).ToList() : page;
            return Html.Render<PostListFragment>(new { Posts = all, NextUrl = next });
        }
        return Html.Render<BlogIndex>(new
        {
            Ctx = ctx, Posts = page, NextUrl = next, Heading = blog.Name, Description = blog.Description, FeedUrl = $"{blogPath}/feed.xml",
        });
    }

    private const string DcNs = "http://purl.org/dc/elements/1.1/";

    private static IResult Feed(RenderContext ctx, HttpRequest req, string title, string description, string path, List<PostSummary> posts)
    {
        var baseUrl = BaseUrl(ctx.Site.BaseUrl, req);
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true }))
        {
            w.WriteStartElement("rss");
            w.WriteAttributeString("version", "2.0");
            w.WriteAttributeString("xmlns", "dc", null, DcNs);
            w.WriteStartElement("channel");
            w.WriteElementString("title", title);
            w.WriteElementString("link", baseUrl + path);
            w.WriteElementString("description", description);
            foreach (var p in posts)
            {
                w.WriteStartElement("item");
                w.WriteElementString("title", p.Title);
                w.WriteElementString("link", baseUrl + p.Url);
                w.WriteElementString("guid", baseUrl + p.Url);
                if (p.PublishedAtUtc is { } d) w.WriteElementString("pubDate", d.ToString("r", CultureInfo.InvariantCulture));
                w.WriteElementString("description", p.Excerpt);
                if (!string.IsNullOrEmpty(p.Author)) w.WriteElementString("creator", DcNs, p.Author);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }
        return Results.Text(sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\""), "application/rss+xml; charset=utf-8");
    }

    private static string BaseUrl(string configured, HttpRequest req) =>
        !string.IsNullOrWhiteSpace(configured) ? configured.TrimEnd('/') : $"{req.Scheme}://{req.Host}";
}

using Homepage.Data;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Content;

/// <summary>An item's own appearance settings (null / inherit = like the page above).</summary>
public sealed record PageOptionValues(bool InheritTheme = true, int? ThemeId = null, PageLayout? Layout = null, bool? NoIndex = null, string CustomCss = "")
{
    public static PageOptionValues Of(ContentItem c) => new(c.InheritTheme, c.ThemeId, c.Layout, c.NoIndex, c.CustomCss);
}

/// <summary>Appearance in effect: the item's own settings over those of the pages above it.</summary>
/// <param name="ThemeId">Null = the site's active theme.</param>
/// <param name="Css">The CSS of the pages above, then the item's own.</param>
public sealed record PageOptions(int? ThemeId, PageLayout Layout, bool NoIndex, string Css)
{
    public static readonly PageOptions SiteDefault = new(null, PageLayout.Full, false, "");

    public PageOptions Apply(PageOptionValues own) => new(
        own.InheritTheme ? ThemeId : own.ThemeId,
        own.Layout ?? Layout,
        own.NoIndex ?? NoIndex,
        string.IsNullOrWhiteSpace(own.CustomCss) ? Css : string.IsNullOrEmpty(Css) ? own.CustomCss : Css + "\n" + own.CustomCss);
}

/// <summary>What a public path points at.</summary>
public abstract record SitePath
{
    public sealed record Page(int Id) : SitePath;
    public sealed record BlogIndex(int BlogId, string Path) : SitePath;
    public sealed record Post(int BlogId, string BlogPath, string Slug) : SitePath;
    public sealed record Feed(int BlogId, string BlogPath) : SitePath;
    /// <summary>An old blog address: 301 to <see cref="To"/>.</summary>
    public sealed record Moved(string To) : SitePath;
    public sealed record None : SitePath;
}

/// <summary>
/// The address tree: pages under pages (/about/team) and blogs under pages (/docs/releases). Built from the database and
/// cached until something changes (<see cref="Changed"/>), so every link and every request uses the same addresses.
/// </summary>
public sealed class SiteTree(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>How deep pages can be nested. Only a guard against runaway data; loops are refused separately.</summary>
    public const int MaxDepth = 64;
    private static int _version;
    private sealed record Snapshot(int Version, SiteMap Map);
    private volatile Snapshot? _cached;

    /// <summary>Call after pages or blogs are created, renamed, moved or deleted.</summary>
    public static void Changed() => Interlocked.Increment(ref _version);

    public async Task<SiteMap> GetAsync(CancellationToken ct = default)
    {
        var v = Volatile.Read(ref _version);
        if (_cached is { } c && c.Version == v) return c.Map;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pages = await db.ContentItems.AsNoTracking().Where(x => x.Type == ContentType.Page)
            .Select(x => new SiteMap.Node(x.Id, x.ParentId, x.Slug, x.Status == ContentStatus.Published, x.Title,
                new PageOptionValues(x.InheritTheme, x.ThemeId, x.Layout, x.NoIndex, x.CustomCss))).ToListAsync(ct);
        var blogs = await db.Blogs.AsNoTracking().Select(b => new SiteMap.BlogNode(b.Id, b.ParentPageId, b.Slug, b.PreviousSlugs, b.Name)).ToListAsync(ct);
        var posts = await db.ContentItems.AsNoTracking().Where(x => x.Type == ContentType.Post)
            .Select(x => new SiteMap.PostNode(x.Id, x.BlogId ?? 0, x.Slug, x.Status == ContentStatus.Published, x.Title)).ToListAsync(ct);
        var map = new SiteMap(pages, blogs, posts);
        _cached = new Snapshot(v, map);
        return map;
    }

    /// <summary>The path of a page from the database ("/a/b"), for checks inside a write (no cache).</summary>
    public static async Task<string> PagePathAsync(AppDbContext db, int pageId, CancellationToken ct = default)
    {
        var parts = new List<string>();
        int? id = pageId;
        for (var depth = 0; id is { } cur && depth <= MaxDepth; depth++)
        {
            var row = await db.ContentItems.AsNoTracking().Where(x => x.Id == cur).Select(x => new { x.Slug, x.ParentId }).FirstOrDefaultAsync(ct);
            if (row is null) break;
            parts.Insert(0, row.Slug);
            id = row.ParentId;
        }
        return "/" + string.Join('/', parts);
    }
}

public sealed class SiteMap
{
    public sealed record Node(int Id, int? ParentId, string Slug, bool Published = true, string Title = "", PageOptionValues? Options = null);
    public sealed record BlogNode(int Id, int? ParentPageId, string Slug, List<string> Previous, string Name = "");
    public sealed record PostNode(int Id, int BlogId, string Slug, bool Published = true, string Title = "");

    private readonly Dictionary<int, Node> _pages;
    private readonly Dictionary<int, BlogNode> _blogs;
    private readonly Dictionary<int, PostNode> _posts;
    private readonly Dictionary<int, string> _pagePaths = [];
    private readonly Dictionary<int, string> _blogPaths = [];
    private readonly Dictionary<string, int> _pageByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _blogByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _blogByPrevious = new(StringComparer.Ordinal);

    public SiteMap(IEnumerable<Node> pages, IEnumerable<BlogNode> blogs, IEnumerable<PostNode>? posts = null)
    {
        _pages = pages.ToDictionary(p => p.Id);
        _posts = (posts ?? []).ToDictionary(p => p.Id);
        foreach (var p in _pages.Values)
        {
            var path = PagePath(p.Id);
            _pageByPath.TryAdd(path, p.Id);
        }
        _blogs = blogs.ToDictionary(b => b.Id);
        foreach (var b in _blogs.Values)
        {
            var path = (b.ParentPageId is { } pid && _pages.ContainsKey(pid) ? PagePath(pid) : "") + "/" + b.Slug;
            _blogPaths[b.Id] = path;
            _blogByPath.TryAdd(path, b.Id);
            foreach (var prev in b.Previous) _blogByPrevious.TryAdd("/" + prev.Trim('/'), b.Id);
        }
    }

    /// <summary>"/a/b" for a page (its slug chain); unknown pages give "/" + nothing.</summary>
    public string PagePath(int id)
    {
        if (_pagePaths.TryGetValue(id, out var cached)) return cached;
        var parts = new List<string>();
        var seen = new HashSet<int>();
        int? cur = id;
        while (cur is { } c && _pages.TryGetValue(c, out var node) && seen.Add(c) && parts.Count <= SiteTree.MaxDepth)
        {
            parts.Insert(0, node.Slug);
            cur = node.ParentId;
        }
        return _pagePaths[id] = "/" + string.Join('/', parts);
    }

    public string BlogPath(int blogId) => _blogPaths.GetValueOrDefault(blogId, "/blog");

    private readonly Dictionary<int, PageOptions> _options = [];

    /// <summary>The options in effect on a page (its own over its ancestors'); null = the site's defaults.</summary>
    public PageOptions Effective(int? pageId)
    {
        if (pageId is not { } id || !_pages.TryGetValue(id, out var node)) return PageOptions.SiteDefault;
        if (_options.TryGetValue(id, out var cached)) return cached;
        // Walk up (guarding against loops), then apply from the top down.
        var chain = new List<Node>();
        var seen = new HashSet<int>();
        for (Node? n = node; n is not null && seen.Add(n.Id) && chain.Count <= SiteTree.MaxDepth; n = n.ParentId is { } p ? _pages.GetValueOrDefault(p) : null)
            chain.Add(n);
        var result = PageOptions.SiteDefault;
        for (var i = chain.Count - 1; i >= 0; i--) result = result.Apply(chain[i].Options ?? new PageOptionValues());
        return _options[id] = result;
    }

    /// <summary>The page whose options an item inherits: a page's parent, or the page a post's blog is under.</summary>
    public int? InheritsFrom(ContentType type, int? parentId, int? blogId) =>
        type == ContentType.Page ? parentId : blogId is { } b && _blogs.TryGetValue(b, out var blog) ? blog.ParentPageId : null;

    /// <summary>The options in effect for an item with these own settings (works for unsaved values too).</summary>
    public PageOptions EffectiveFor(ContentType type, int? parentId, int? blogId, PageOptionValues own) =>
        Effective(InheritsFrom(type, parentId, blogId)).Apply(own);

    /// <summary>The options of a blog's post list: those of the page it is under.</summary>
    public PageOptions ForBlog(int blogId) => Effective(_blogs.TryGetValue(blogId, out var b) ? b.ParentPageId : null);

    /// <summary>
    /// The address to put in a page for a link: references ("page:12", "post:5", "blog:3") become the current address
    /// of what they point at; anything else is returned as it is. Null when the reference points at nothing, or (with
    /// <paramref name="publishedOnly"/>) at something that is not published.
    /// </summary>
    public string? Href(string url, bool publishedOnly)
    {
        if (!Blocks.SafeUrlAttribute.TryParseReference(url, out var kind, out var id)) return url;
        return kind switch
        {
            "page" when _pages.TryGetValue(id, out var p) && (p.Published || !publishedOnly) => PagePath(id),
            "post" when _posts.TryGetValue(id, out var post) && (post.Published || !publishedOnly) && _blogs.ContainsKey(post.BlogId) => BlogPath(post.BlogId) + "/" + post.Slug,
            "blog" when _blogs.ContainsKey(id) => BlogPath(id),
            _ => null,
        };
    }

    /// <summary>What a reference points at, for the admin ("Team · /about/team"); null for plain addresses or missing targets.</summary>
    public (string Title, string Path, bool Published)? Describe(string url)
    {
        if (!Blocks.SafeUrlAttribute.TryParseReference(url, out var kind, out var id)) return null;
        return kind switch
        {
            "page" when _pages.TryGetValue(id, out var p) => (p.Title, PagePath(id), p.Published),
            "post" when _posts.TryGetValue(id, out var post) => (post.Title, BlogPath(post.BlogId) + "/" + post.Slug, post.Published),
            "blog" when _blogs.TryGetValue(id, out var b) => (b.Name, BlogPath(id), true),
            _ => null,
        };
    }

    /// <summary>The public URL of a page or post.</summary>
    public string Url(ContentType type, int id, string slug, int? blogId) =>
        type == ContentType.Page ? (_pages.ContainsKey(id) ? PagePath(id) : "/" + slug) : BlogPath(blogId ?? 0) + "/" + slug;

    /// <summary>Ids of a page and every page under it.</summary>
    public HashSet<int> PageAndDescendants(int id)
    {
        var result = new HashSet<int> { id };
        bool added;
        do
        {
            added = false;
            foreach (var p in _pages.Values)
                if (p.ParentId is { } parent && result.Contains(parent) && result.Add(p.Id)) added = true;
        } while (added);
        return result;
    }

    public int Depth(int id) => PagePath(id).Count(c => c == '/');

    public SitePath Resolve(string path)
    {
        path = "/" + path.Trim('/');
        if (_pageByPath.TryGetValue(path, out var page)) return new SitePath.Page(page);
        if (_blogByPath.TryGetValue(path, out var blog)) return new SitePath.BlogIndex(blog, path);
        var cut = path.LastIndexOf('/');
        if (cut > 0)
        {
            var (prefix, last) = (path[..cut], path[(cut + 1)..]);
            if (_blogByPath.TryGetValue(prefix, out var b))
                return last == "feed.xml" ? new SitePath.Feed(b, prefix) : new SitePath.Post(b, prefix, last);
            if (_blogByPrevious.TryGetValue(prefix, out var moved)) return new SitePath.Moved(BlogPath(moved) + "/" + last);
        }
        if (_blogByPrevious.TryGetValue(path, out var m)) return new SitePath.Moved(BlogPath(m));
        return new SitePath.None();
    }
}

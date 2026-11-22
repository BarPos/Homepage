using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using Homepage.Accounts;
using Homepage.Blocks;
using Homepage.Content;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homepage.Tests;

public class PageUnitTests
{
    [Fact]
    public void MarkdownLinksCanOpenInANewTab()
    {
        var md = new MarkdownRenderer();
        Assert.Contains("<a href=\"https://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">Docs</a>", md.ToHtml("[Docs](https://example.com){new}"));
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", md.ToHtml("[A](https://a.example){target=_blank}"));
        Assert.DoesNotContain("target", md.ToHtml("[B](https://b.example)"));
        Assert.DoesNotContain("target", md.ToHtml("[C](https://c.example){target=_top}"));
        Assert.DoesNotContain("javascript", md.ToHtml("[x](javascript:alert(1)){new}"));
        Assert.Contains("target=\"_blank\"", md.ToHtml("[A](https://a.example){target=\\_blank}")); // as the rich text editor writes it
    }

    [Fact]
    public void IconsComeFromTheBundledSet()
    {
        Assert.True(Icons.Count > 2500);
        Assert.Equal("solid:house", Icons.Search("house")[0].Key);
        Assert.Contains(Icons.Search("home"), i => i.Key == "solid:house"); // search terms
        Assert.Contains(Icons.Search("github"), i => i.Key == "brands:github");
        var svg = Icons.Svg(Icons.Get("solid:house")!, "btn-icon");
        Assert.StartsWith("<svg class=\"btn-icon\" viewBox=\"0 0 ", svg);
        Assert.Contains("fill=\"currentColor\"", svg);
        Assert.Null(Icons.Get("solid:\"><script>"));
        var def = BlockRegistry.Get("button")!;
        Assert.Empty(BlockValidator.ValidateProps(def, BlockOps.ToJson(new ButtonProps { Text = "Go", Url = "/", Icon = "solid:house" })));
        Assert.NotEmpty(BlockValidator.ValidateProps(def, BlockOps.ToJson(new ButtonProps { Text = "Go", Url = "/", Icon = "solid:no-such-icon" })));
    }

    [Fact]
    public void LayoutBlocksNestMoreFreely()
    {
        var bar = BlockRegistry.Get("sitebar")!;
        foreach (var t in new[] { "columns", "group", "heading", "image", "divider", "quote", "menu", "sitebrand", "button" })
            Assert.True(bar.AllowsChild(t), t);
        Assert.False(bar.AllowsChild("section"));
        Assert.False(bar.AllowsChild("sitebar"));
        var column = BlockRegistry.Get("column")!;
        Assert.True(column.AllowsChild("columns")); // columns in columns
        Assert.True(column.AllowsChild("group"));
        Assert.True(column.AllowsChild("sitebrand"));
        var group = BlockRegistry.Get("group")!;
        Assert.True(group.AllowsChild("group"));
        Assert.True(group.AllowedUnder("sitebar"));
        Assert.True(BlockRegistry.Get("columns")!.AllowedUnder("sitebar"));
        // a header with a site bar holding columns with a group inside validates
        var tree = new BlockTree { Blocks = [new BlockNode { Type = "sitebar", Props = BlockOps.ToJson(new SiteBarProps()), Children =
            [new BlockNode { Type = "columns", Props = BlockOps.ToJson(new ColumnsProps()), Children =
                [new BlockNode { Type = "column", Props = BlockOps.ToJson(new ColumnProps()), Children =
                    [new BlockNode { Type = "group", Props = BlockOps.ToJson(new GroupProps()), Children =
                        [new BlockNode { Type = "sitebrand", Props = BlockOps.ToJson(new SiteBrandProps()) },
                         new BlockNode { Type = "menu", Props = BlockOps.ToJson(new MenuProps { Orientation = Orientation.Vertical, Collapse = true }) }] }] }] }] }] };
        Assert.Empty(BlockValidator.Validate(tree, TreeKind.Header));
        // an optional background color may be empty; a mandatory one may not
        Assert.Empty(BlockValidator.ValidateProps(BlockRegistry.Get("column")!, BlockOps.ToJson(new ColumnProps { Background = "" })));
        Assert.NotEmpty(BlockValidator.ValidateProps(BlockRegistry.Get("section")!, BlockOps.ToJson(new SectionProps { Background = "" })));
    }

    [Fact]
    public void SavingKeepsSettingsTheFormDoesNotSend()
    {
        var def = BlockRegistry.Get("button")!;
        var current = BlockOps.ToJson(new ButtonProps { Text = "Old", Size = ButtonSize.Large, FullWidth = true, Icon = "solid:house" });
        var form = new Dictionary<string, string> { ["p.Text"] = "New", ["p.Url"] = "/" };
        var bound = BlockPropsBinder.Bind(def, k => form.GetValueOrDefault(k), current);
        var props = bound.Deserialize<ButtonProps>(BlockTree.Json)!;
        Assert.Equal(("New", ButtonSize.Large, true, "solid:house"), (props.Text, props.Size, props.FullWidth, props.Icon));
        // without current settings, missing fields get their defaults
        var fresh = BlockPropsBinder.Bind(def, k => form.GetValueOrDefault(k)).Deserialize<ButtonProps>(BlockTree.Json)!;
        Assert.Equal((ButtonSize.Medium, false), (fresh.Size, fresh.FullWidth));
    }

    [Fact]
    public void ReferencesAndInheritedOptions()
    {
        var map = new SiteMap(
            [
                new SiteMap.Node(1, null, "landing", true, "Landing", new PageOptionValues(false, 7, Homepage.Data.PageLayout.Blank, true, ".a{}")),
                new SiteMap.Node(2, 1, "offer", true, "Offer", new PageOptionValues(CustomCss: ".b{}")),
                new SiteMap.Node(3, 2, "terms", false, "Terms", new PageOptionValues(true, null, Homepage.Data.PageLayout.Full, false)),
                new SiteMap.Node(4, null, "about", true, "About"),
            ],
            [new SiteMap.BlogNode(10, 1, "news", [], "News")],
            [new SiteMap.PostNode(20, 10, "hello", true, "Hello")]);
        // references follow the current address; drafts only in the editor
        Assert.Equal("/landing/offer", map.Href("page:2", publishedOnly: true));
        Assert.Null(map.Href("page:3", publishedOnly: true));
        Assert.Equal("/landing/offer/terms", map.Href("page:3", publishedOnly: false));
        Assert.Equal("/landing/news/hello", map.Href("post:20", true));
        Assert.Equal("/landing/news", map.Href("blog:10", true));
        Assert.Null(map.Href("page:99", true));
        Assert.Equal("https://example.com", map.Href("https://example.com", true));
        Assert.Equal(("Offer", "/landing/offer", true), map.Describe("page:2"));
        // subpages inherit; their own settings win; CSS cascades
        Assert.Equal(new PageOptions(7, Homepage.Data.PageLayout.Blank, true, ".a{}\n.b{}"), map.Effective(2));
        Assert.Equal(new PageOptions(7, Homepage.Data.PageLayout.Full, false, ".a{}\n.b{}"), map.Effective(3));
        Assert.Equal(PageOptions.SiteDefault, map.Effective(4));
        // posts and post lists of a blog under a page inherit from that page
        Assert.Equal(map.Effective(1), map.ForBlog(10));
        Assert.Equal(7, map.EffectiveFor(Homepage.Data.ContentType.Post, null, 10, new PageOptionValues()).ThemeId);
        Assert.Null(map.EffectiveFor(Homepage.Data.ContentType.Post, null, 10, new PageOptionValues(false, null)).ThemeId); // "the site's theme"
    }

    [Fact]
    public void MarkdownLinksToPagesFollowThem()
    {
        var md = new MarkdownRenderer();
        string? Resolve(string url) => url == "page:2" ? "/landing/offer" : url.StartsWith("page:") ? null : url;
        Assert.Contains("<a href=\"/landing/offer\">the offer</a>", md.ToHtml("See [the offer](page:2).", Resolve));
        var gone = md.ToHtml("See [the old page](page:9) here.", Resolve);
        Assert.Contains("See the old page here.", gone); // the words stay, the dead link goes
        Assert.DoesNotContain("<a", gone);
        Assert.DoesNotContain("page:2", md.ToHtml("[x](page:2)")); // without a resolver the sanitizer drops it rather than leak it
    }

    [Fact]
    public void SvgUploadsMustBeSafe()
    {
        static string? Why(string svg) => Homepage.Media.MediaService.WhyUnsafeSvg(System.Text.Encoding.UTF8.GetBytes(svg));
        const string ns = "xmlns=\"http://www.w3.org/2000/svg\"";
        Assert.Null(Why($"<svg {ns} viewBox=\"0 0 32 32\"><style>@media (prefers-color-scheme:dark){{path{{fill:#fff}}}}</style><path d=\"M0 0h32v32z\"/></svg>"));
        Assert.Null(Why($"<?xml version=\"1.0\"?><svg {ns}><defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\" width=\"1\" height=\"1\"/><use href=\"#g\"/></svg>"));
        Assert.NotNull(Why($"<svg {ns}><script>alert(1)</script></svg>"));
        Assert.NotNull(Why($"<svg {ns} onload=\"alert(1)\"/>"));
        Assert.NotNull(Why($"<svg {ns}><foreignObject><div xmlns=\"http://www.w3.org/1999/xhtml\">x</div></foreignObject></svg>"));
        Assert.NotNull(Why($"<svg {ns}><a href=\"javascript:alert(1)\"><text>x</text></a></svg>"));
        Assert.NotNull(Why($"<svg {ns}><image href=\"https://evil.example/x.png\"/></svg>"));
        Assert.NotNull(Why($"<svg {ns}><rect style=\"fill:url('https://evil.example/x')\"/></svg>"));
        Assert.NotNull(Why($"<svg {ns}><style>@import url(https://evil.example/x.css);</style></svg>"));
        Assert.NotNull(Why("<!DOCTYPE svg [<!ENTITY x \"y\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>"));
        // what editors export: a DOCTYPE and Inkscape/RDF metadata are fine
        Assert.Null(Why("<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:sodipodi=\"http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"" +
            " xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" inkscape:version=\"1.3\"><sodipodi:namedview id=\"n\"/><metadata><rdf:RDF/></metadata><path d=\"M0 0h1v1z\"/></svg>"));
        Assert.NotNull(Why($"<svg {ns}><a href=\"#x\"><animate attributeName=\"href\" values=\"javascript:alert(1)\"/><text>x</text></a></svg>"));
        Assert.NotNull(Why($"<svg {ns}><set attributeName=\"fill\" to=\"java&#x09;script:alert(1)\"/></svg>"));
        Assert.NotNull(Why("<html><body>not svg</body></html>"));
        Assert.NotNull(Why("<svg>no namespace</svg>"));
    }

    [Fact]
    public void SiteMapResolvesNestedAddresses()
    {
        var map = new SiteMap(
            [new SiteMap.Node(1, null, "about"), new SiteMap.Node(2, 1, "team"), new SiteMap.Node(3, 2, "jobs")],
            [new SiteMap.BlogNode(10, null, "blog", []), new SiteMap.BlogNode(11, 1, "news", ["news", "old/news"])]);
        Assert.Equal("/about/team/jobs", map.PagePath(3));
        Assert.Equal("/about/news", map.BlogPath(11));
        Assert.Equal(new SitePath.Page(3), map.Resolve("about/team/jobs/"));
        Assert.Equal(new SitePath.BlogIndex(11, "/about/news"), map.Resolve("/about/news"));
        Assert.Equal(new SitePath.Post(11, "/about/news", "hello"), map.Resolve("/about/news/hello"));
        Assert.Equal(new SitePath.Feed(11, "/about/news"), map.Resolve("/about/news/feed.xml"));
        Assert.Equal(new SitePath.Moved("/about/news/hello"), map.Resolve("/old/news/hello"));
        Assert.Equal(new SitePath.Moved("/about/news"), map.Resolve("/news"));
        Assert.Equal(new SitePath.None(), map.Resolve("/team"));
        Assert.Equal([2, 3], map.PageAndDescendants(2).Order());
        Assert.Equal("/about/news/hello", map.Url(Homepage.Data.ContentType.Post, 99, "hello", 11));
    }
}

/// <summary>Over HTTP: subpages, blogs under pages, page options (theme, layout, CSS, no-index) and button icons.</summary>
[Collection("Postgres"), Trait("Category", "Postgres")]
public sealed class PagesHttpTests(PostgresFixture pg)
{
    private const string Password = "test-password-123";

    [Fact]
    public async Task NestedPagesBlogsAndPageOptions()
    {
        var cs = await pg.CreateDatabaseAsync();
        var data = Directory.CreateTempSubdirectory("homepage-test-").FullName;
        Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", cs);
        Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", data);
        try
        {
            // Static web assets (the /_content/… files) as in development, so the test sees the files the site links to.
            await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing").UseStaticWebAssets());
            using (var scope = app.Services.CreateScope())
                Assert.True((await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync("admin", "Admin", Password, "test", makeAdmin: true)).Result.Ok);
            var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            var token = Token(await client.GetStringAsync("/admin/login"));
            await client.PostAsync("/admin/login", Form(("__RequestVerificationToken", token), ("userName", "admin"), ("password", Password)));
            var dashboard = await client.GetStringAsync("/admin");
            token = Token(dashboard);
            var gm = Regex.Match(dashboard, "name=\"groupId\"(?:\\s+value=\"(\\d+)\"|>\\s*<option value=\"(\\d+)\")");
            var groupId = gm.Groups[1].Success ? gm.Groups[1].Value : gm.Groups[2].Value;

            async Task<string> Create(string title, string type, string? parent = null, string? blog = null)
            {
                var fields = new List<(string, string)> { ("__RequestVerificationToken", token), ("title", title), ("type", type), ("groupId", groupId) };
                if (parent is not null) fields.Add(("parentId", parent));
                if (blog is not null) fields.Add(("blogId", blog));
                var res = await client.PostAsync("/admin/content/new", Form([.. fields]));
                Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
                var id = res.Headers.Location!.ToString().Split('-').Last();
                Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync($"/admin/content/{id}/publish", Form(("__RequestVerificationToken", token)))).StatusCode);
                return id;
            }

            // static files are not taken by the page route (/{**path})
            var css = await client.GetAsync("/_content/Homepage.Web/css/site.css");
            Assert.Equal(HttpStatusCode.OK, css.StatusCode);
            Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);

            // subpages
            var about = await Create("About", "Page");
            var team = await Create("Team", "Page", parent: about);
            var jobs = await Create("Jobs", "Page", parent: team);
            Assert.Contains("Team", await client.GetStringAsync("/about/team"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/about/team/jobs")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/team")).StatusCode);
            var top = await Create("Team", "Page"); // the same slug at the top level is fine: another parent
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/team")).StatusCode);
            Assert.Contains("/about/team/jobs", await client.GetStringAsync("/admin/content?type=page"));

            // a page cannot go under itself or its subpages; a page with subpages cannot be deleted
            var settings = await client.PostAsync($"/admin/content/{about}", Form(("__RequestVerificationToken", token), ("title", "About"), ("slug", "about"),
                ("excerpt", ""), ("groupId", groupId), ("parentId", jobs)));
            Assert.Contains("cannot be under itself", await settings.Content.ReadAsStringAsync());
            await client.PostAsync($"/admin/content/{about}/delete", Form(("__RequestVerificationToken", token)));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/about")).StatusCode);
            // moving a subpage changes its address
            await client.PostAsync($"/admin/content/{jobs}", Form(("__RequestVerificationToken", token), ("title", "Jobs"), ("slug", "jobs"),
                ("excerpt", ""), ("groupId", groupId), ("parentId", about)));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/about/jobs")).StatusCode);

            // a blog under a page, its posts and feed; moving it keeps the old addresses working
            var blogRes = await client.PostAsync("/admin/blogs", Form(("__RequestVerificationToken", token), ("name", "News"), ("slug", "news"),
                ("description", ""), ("postsPerPage", "10"), ("parentPageId", about)));
            var blogId = blogRes.Headers.Location!.ToString().Split('/').Last();
            var post = await Create("Launch day", "Post", blog: blogId);
            Assert.Contains("Launch day", await client.GetStringAsync("/about/news"));
            Assert.Contains("Launch day", await client.GetStringAsync("/about/news/launch-day"));
            Assert.Contains("/about/news/launch-day", await client.GetStringAsync("/about/news/feed.xml"));
            Assert.Contains("/about/news", await client.GetStringAsync("/admin/blogs"));
            var clash = await client.PostAsync("/admin/blogs", Form(("__RequestVerificationToken", token), ("name", "Team"), ("slug", "team"),
                ("postsPerPage", "10"), ("parentPageId", about)));
            Assert.Contains("already used by a page", await clash.Content.ReadAsStringAsync());
            var move = await client.PostAsync("/admin/blogs", Form(("__RequestVerificationToken", token), ("id", blogId), ("name", "News"), ("slug", "news"),
                ("description", ""), ("postsPerPage", "10"), ("parentPageId", "")));
            Assert.True(move.StatusCode == HttpStatusCode.Redirect, $"{move.StatusCode}: {Regex.Match(await move.Content.ReadAsStringAsync(), "inline-alert\">([^<]*)").Groups[1].Value}");
            var moved = await client.GetAsync("/about/news/launch-day");
            Assert.Equal(HttpStatusCode.MovedPermanently, moved.StatusCode);
            Assert.Equal("/news/launch-day", moved.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/news/launch-day")).StatusCode);
            // renaming a page moves the blogs under it, and their old addresses redirect
            await client.PostAsync("/admin/blogs", Form(("__RequestVerificationToken", token), ("id", blogId), ("name", "News"), ("slug", "news"),
                ("description", ""), ("postsPerPage", "10"), ("parentPageId", jobs)));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/about/jobs/news")).StatusCode);
            await client.PostAsync($"/admin/content/{about}", Form(("__RequestVerificationToken", token), ("title", "About"), ("slug", "company"),
                ("excerpt", ""), ("groupId", groupId), ("parentId", "0")));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/company/jobs/news/launch-day")).StatusCode);
            var renamed = await client.GetAsync("/about/jobs/news/launch-day");
            Assert.Equal("/company/jobs/news/launch-day", renamed.Headers.Location?.ToString());
            Assert.Equal(HttpStatusCode.MovedPermanently, (await client.GetAsync("/news")).StatusCode); // the earlier move still redirects
            await client.PostAsync($"/admin/content/{about}", Form(("__RequestVerificationToken", token), ("title", "About"), ("slug", "about"),
                ("excerpt", ""), ("groupId", groupId), ("parentId", "0")));
            await client.PostAsync("/admin/blogs", Form(("__RequestVerificationToken", token), ("id", blogId), ("name", "News"), ("slug", "news"),
                ("description", ""), ("postsPerPage", "10"), ("parentPageId", "")));

            // page options: own theme, blank layout, custom CSS, no-index
            var themes = await client.GetStringAsync("/admin/themes");
            var otherTheme = Regex.Matches(themes, "/admin/themes/(\\d+)/preview").Select(m => m.Groups[1].Value).Last();
            var before = await client.GetStringAsync("/about/team");
            Assert.Contains("class=\"site-top\"", before);
            Assert.DoesNotContain("noindex", before);
            await client.PostAsync($"/admin/content/{team}/options", Form(("__RequestVerificationToken", token), ("theme", otherTheme), ("layout", "Blank"),
                ("noIndex", "true"), ("customCss", ".site-main{outline:1px solid red}</style><script>")));
            var page = await client.GetStringAsync("/about/team");
            Assert.Contains("<meta name=\"robots\" content=\"noindex\"", page);
            Assert.DoesNotContain("class=\"site-top\"", page);
            Assert.DoesNotContain("class=\"site-footer\"", page);
            Assert.Contains($"/theme/{otherTheme}/", page);
            Assert.Contains(".site-main{outline:1px solid red}", page);
            Assert.DoesNotContain("<script>", page);
            var sitemap = await client.GetStringAsync("/sitemap.xml");
            Assert.DoesNotContain("/about/team<", sitemap);
            Assert.Contains("/about/jobs<", sitemap);
            Assert.Contains("/news<", sitemap);
            Assert.Contains("Saved.", await client.GetStringAsync($"/admin/content/{team}"));

            // a page with its own (not active) theme follows that theme's publishes
            var copy = await client.PostAsync($"/admin/themes/{otherTheme}/duplicate", Form(("__RequestVerificationToken", token)));
            var copyId = copy.Headers.Location!.ToString().Split('/').Last();
            await client.PostAsync($"/admin/content/{team}/options", Form(("__RequestVerificationToken", token), ("theme", copyId), ("layout", "Full"),
                ("noIndex", "false"), ("customCss", "")));
            var cssBefore = Regex.Match(await client.GetStringAsync("/about/team"), $"/theme/{copyId}/(\\d+)\\.css").Groups[1].Value;
            var copyEditor = await client.GetStringAsync($"/admin/themes/{copyId}");
            var copyName = Regex.Match(copyEditor, "id=\"tn\" name=\"name\" type=\"text\" value=\"([^\"]+)\"").Groups[1].Value;
            await client.PostAsync($"/admin/themes/{copyId}", Form(("__RequestVerificationToken", token), ("name", copyName), ("customCss", "body{outline:0}")));
            await client.PostAsync($"/admin/themes/{copyId}/publish", Form(("__RequestVerificationToken", token)));
            var afterHtml = await client.GetStringAsync("/about/team");
            var cssAfter = Regex.Match(afterHtml, $"/theme/{copyId}/(\\d+)\\.css[^\"]*").Value;
            Assert.NotEqual(cssBefore, Regex.Match(cssAfter, "/(\\d+)\\.css").Groups[1].Value);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(System.Net.WebUtility.HtmlDecode(cssAfter))).StatusCode);

            // subpages inherit the page options unless they set their own
            await client.PostAsync($"/admin/content/{about}/options", Form(("__RequestVerificationToken", token), ("theme", "inherit"), ("layout", "NoFooter"),
                ("noIndex", "true"), ("customCss", ".about{color:red}")));
            var jobsHtml = await client.GetStringAsync("/about/jobs");
            Assert.Contains("<meta name=\"robots\" content=\"noindex\"", jobsHtml);
            Assert.DoesNotContain("class=\"site-footer\"", jobsHtml);
            Assert.Contains(".about{color:red}", jobsHtml);
            Assert.Contains("class=\"site-top\"", jobsHtml);
            await client.PostAsync($"/admin/content/{jobs}/options", Form(("__RequestVerificationToken", token), ("theme", "inherit"), ("layout", "inherit"),
                ("noIndex", "false"), ("customCss", ".jobs{color:blue}")));
            jobsHtml = await client.GetStringAsync("/about/jobs");
            Assert.DoesNotContain("noindex", jobsHtml);
            Assert.DoesNotContain("class=\"site-footer\"", jobsHtml);
            Assert.Matches(@"\.about\{color:red\}\s*\.jobs\{color:blue\}", jobsHtml);
            var jobsSettings = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/content/{jobs}"));
            Assert.Contains("Same as the parent page (No footer)", jobsSettings);
            Assert.Contains("Same as the parent page (not indexed)", jobsSettings);
            Assert.Contains("/about/jobs<", await client.GetStringAsync("/sitemap.xml"));
            Assert.DoesNotContain("/about<", await client.GetStringAsync("/sitemap.xml"));
            await client.PostAsync($"/admin/content/{about}/options", Form(("__RequestVerificationToken", token), ("theme", "inherit"), ("layout", "inherit"),
                ("noIndex", "inherit"), ("customCss", "")));
            await client.PostAsync($"/admin/content/{jobs}/options", Form(("__RequestVerificationToken", token), ("theme", "inherit"), ("layout", "inherit"),
                ("noIndex", "inherit"), ("customCss", "")));

            // someone who cannot see a page's parent can still save it without moving it
            var secretGroup = (await client.PostAsync("/admin/groups", Form(("__RequestVerificationToken", token), ("name", "secret"), ("comment", ""), ("valid", "true"))))
                .Headers.Location!.ToString().Split('/').Last();
            var secretRes = await client.PostAsync("/admin/content/new", Form(("__RequestVerificationToken", token), ("title", "Secret"), ("type", "Page"), ("groupId", secretGroup)));
            var secret = secretRes.Headers.Location!.ToString().Split('-').Last();
            var child = await Create("Visible child", "Page", parent: secret);
            using (var scope = app.Services.CreateScope())
            {
                var (r, editor) = await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync("editor", "Editor", Password, "test");
                Assert.True(r.Ok);
                await using var db = await scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Homepage.Data.AppDbContext>>().CreateDbContextAsync();
                db.UserGroups.Add(new Homepage.Data.UserGroup { UserId = editor!.Id, GroupId = int.Parse(groupId), Permission = Homepage.Permissions.PermNames.ToKey(Homepage.Permissions.Perm.Rw) });
                await db.SaveChangesAsync();
            }
            var other = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
            var t2 = Token(await other.GetStringAsync("/admin/login"));
            await other.PostAsync("/admin/login", Form(("__RequestVerificationToken", t2), ("userName", "editor"), ("password", Password)));
            var childSettings = await other.GetStringAsync($"/admin/content/{child}");
            t2 = Token(childSettings);
            Assert.True(Regex.IsMatch(System.Net.WebUtility.HtmlDecode(childSettings), $"<option value=\"{secret}\" selected[^>]*>/secret · \\(a page you cannot see\\)</option>"),
                Regex.Match(childSettings, "<select id=\"pp\".*?</select>", RegexOptions.Singleline).Value);
            var parentField = Regex.Match(childSettings, "<select id=\"pp\" name=\"parentId\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value;
            var selectedParent = Regex.Match(parentField, "<option value=\"(\\d+)\" selected").Groups[1].Value;
            await other.PostAsync($"/admin/content/{child}", Form(("__RequestVerificationToken", t2), ("title", "Visible child"), ("slug", "visible-child"),
                ("excerpt", "x"), ("groupId", groupId), ("parentId", selectedParent)));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/secret/visible-child")).StatusCode);

            // a button with an icon that opens in a new tab
            var op = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{about}/ops")
            {
                Content = new StringContent("""{"op":"insert","type":"button","parentId":null,"index":1}""", System.Text.Encoding.UTF8, "application/json"),
            };
            op.Headers.Add("RequestVerificationToken", token);
            op.Headers.Add("Accept", "application/json");
            var blockId = Regex.Match(await (await client.SendAsync(op)).Content.ReadAsStringAsync(), "\"newId\":\"([^\"]+)\"").Groups[1].Value;
            async Task<HttpStatusCode> Props(string icon)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{about}/props/{blockId}")
                {
                    Content = Form(("p.Text", "GitHub"), ("p.Url", "https://github.com"), ("p.Style", "Primary"), ("p.Align", "Left"), ("p.NewTab", "true"),
                        ("p.Icon", icon), ("p.IconMediaId", ""), ("p.IconPosition", "AfterText")),
                };
                req.Headers.Add("RequestVerificationToken", token);
                req.Headers.Add("HX-Request", "true");
                return (await client.SendAsync(req)).StatusCode;
            }
            Assert.NotEqual(HttpStatusCode.NoContent, await Props("solid:not-an-icon"));
            Assert.Equal(HttpStatusCode.NoContent, await Props("brands:github"));
            await client.PostAsync($"/admin/content/{about}/publish", Form(("__RequestVerificationToken", token)));
            var aboutHtml = await client.GetStringAsync("/about");
            Assert.Matches("<a class=\"btn btn-primary btn-medium\\s*\" href=\"https://github.com\" target=\"_blank\" rel=\"noopener noreferrer\">\\s*<span>GitHub</span>\\s*<svg class=\"btn-icon\"", aboutHtml);
            Assert.Contains("(opens in a new tab)", aboutHtml);
            Assert.Contains("data-icon-value=\"solid:house\"", await client.GetStringAsync("/admin/editor/icons?q=house"));

            // the Markdown block gets the switchable editor
            var md = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{about}/ops")
            {
                Content = new StringContent("""{"op":"insert","type":"markdown","parentId":null,"index":1}""", System.Text.Encoding.UTF8, "application/json"),
            };
            md.Headers.Add("RequestVerificationToken", token);
            md.Headers.Add("Accept", "application/json");
            var mdId = Regex.Match(await (await client.SendAsync(md)).Content.ReadAsStringAsync(), "\"newId\":\"([^\"]+)\"").Groups[1].Value;
            Assert.Contains("data-md-editor", await client.GetStringAsync($"/admin/editor/content-{about}/inspector/{mdId}"));
            Assert.Contains("toastui-editor.min.js", await client.GetStringAsync($"/admin/editor/content-{about}"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_content/Homepage.Web/lib/toastui/toastui-editor.min.js")).StatusCode);

            // menu links with an icon, opening in a new tab
            var menu = await client.PostAsync("/admin/menus/main", Form(("__RequestVerificationToken", token), ("name", "Main menu"),
                ("icon", "brands:github"), ("label", "Code"), ("url", "https://github.com"), ("newTab", "true"),
                ("icon", ""), ("label", "Home"), ("url", "/"), ("newTab", "false")));
            Assert.Equal(HttpStatusCode.Redirect, menu.StatusCode);
            var withMenu = await client.GetStringAsync("/blog");
            Assert.Matches("<a href=\"https://github.com\" target=\"_blank\" rel=\"noopener noreferrer\"><svg class=\"menu-icon\"[^>]*>.*?</svg><span>Code</span>", withMenu);
            Assert.Contains("<a href=\"/\"><span>Home</span></a>", withMenu);
            var badIcon = await client.PostAsync("/admin/menus/main", Form(("__RequestVerificationToken", token), ("name", "Main menu"),
                ("icon", "solid:nope"), ("label", "X"), ("url", "/"), ("newTab", "false")));
            Assert.Contains("not a known icon", await badIcon.Content.ReadAsStringAsync());

            // favicons in three formats; an unsafe SVG is refused
            async Task<HttpStatusCode> Upload(string name, byte[] bytes)
            {
                var multipart = new MultipartFormDataContent
                {
                    { new StringContent(token), "__RequestVerificationToken" }, { new StringContent(groupId), "groupId" },
                    { new ByteArrayContent(bytes), "files", name },
                };
                return (await client.PostAsync("/admin/media", multipart)).StatusCode;
            }
            Assert.Equal(HttpStatusCode.Redirect, await Upload("icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><rect width=\"1\" height=\"1\"/></svg>"u8.ToArray()));
            Assert.NotEqual(HttpStatusCode.Redirect, await Upload("evil.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"u8.ToArray()));
            Assert.Equal(HttpStatusCode.Redirect, await Upload("icon.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
            Assert.Equal(HttpStatusCode.Redirect, await Upload("favicon.ico", [0x00, 0x00, 0x01, 0x00, 1, 0, 16, 16, 0, 0, 0, 0]));
            Dictionary<string, (int Id, string Stored)> media;
            using (var scope = app.Services.CreateScope())
            await using (var db = await scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Homepage.Data.AppDbContext>>().CreateDbContextAsync())
                media = db.Media.ToList().ToDictionary(m => m.ContentType, m => (m.Id, m.StoredName));
            Assert.False(media.ContainsKey("application/octet-stream"));
            var settingsPage = await client.GetStringAsync("/admin/settings");
            var save = await client.PostAsync("/admin/settings", Form(("__RequestVerificationToken", Token(settingsPage)), ("title", "My site"), ("tagline", ""),
                ("baseUrl", ""), ("home", ""), ("defaultGroupId", groupId), ("logoMediaId", ""),
                ("faviconMediaId", media["image/png"].Id.ToString()), ("faviconSvgMediaId", media["image/svg+xml"].Id.ToString()),
                ("faviconIcoMediaId", media["image/x-icon"].Id.ToString())));
            Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
            var head = WebUtility.HtmlDecode(await client.GetStringAsync("/blog"));
            Assert.Contains($"<link rel=\"icon\" href=\"/media/{media["image/x-icon"].Stored}\" sizes=\"32x32\"", head);
            Assert.Contains($"<link rel=\"icon\" href=\"/media/{media["image/svg+xml"].Stored}\" type=\"image/svg+xml\"", head);
            Assert.Contains($"<link rel=\"apple-touch-icon\" href=\"/media/{media["image/png"].Stored}\"", head);
            var fav = await client.GetAsync("/favicon.ico");
            Assert.Equal($"/media/{media["image/x-icon"].Stored}", fav.Headers.Location?.ToString());
            var svgFile = await client.GetAsync($"/media/{media["image/svg+xml"].Stored}");
            Assert.Equal("image/svg+xml", svgFile.Content.Headers.ContentType?.MediaType);
            Assert.Contains("sandbox", svgFile.Headers.GetValues("Content-Security-Policy").Single());
            var wrongType = await client.PostAsync("/admin/settings", Form(("__RequestVerificationToken", Token(settingsPage)), ("title", "My site"),
                ("defaultGroupId", groupId), ("faviconSvgMediaId", media["image/png"].Id.ToString())));
            Assert.NotEqual(HttpStatusCode.Redirect, wrongType.StatusCode);
            var inspector = await client.GetStringAsync($"/admin/editor/content-{about}/inspector/{blockId}");
            Assert.Contains("data-icon-for", inspector);
            Assert.Contains("data-link-for=\"f-Url\"", inspector); // the link field has a page/blog/post chooser
            Assert.Contains("id=\"link-dialog\"", inspector);
            var links = await client.GetStringAsync("/admin/editor/links?q=team");
            Assert.Contains($"data-link-value=\"page:{team}\"", links); // a reference that follows the page
            Assert.Contains("<code>/about/team</code>", links);
            Assert.Contains($"data-link-value=\"page:{top}\"", links);
            Assert.DoesNotContain("/about/news", links);
            Assert.Contains($"data-link-value=\"post:{post}\"", await client.GetStringAsync("/admin/editor/links?q=launch"));
            Assert.Contains("id=\"link-dialog\"", await client.GetStringAsync("/admin/menus/main"));

            // a button linking to a page by reference follows the page when it is renamed and disappears when it is unpublished
            var refReq = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{about}/props/{blockId}")
            {
                Content = Form(("p.Text", "Jobs"), ("p.Url", $"page:{jobs}"), ("p.Style", "Primary"), ("p.Align", "Left"), ("p.NewTab", "false"),
                    ("p.Icon", ""), ("p.IconMediaId", ""), ("p.IconPosition", "BeforeText")),
            };
            refReq.Headers.Add("RequestVerificationToken", token);
            refReq.Headers.Add("HX-Request", "true");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(refReq)).StatusCode);
            Assert.Contains("→ Jobs · /about/jobs", System.Net.WebUtility.HtmlDecode(await client.GetStringAsync($"/admin/editor/content-{about}/inspector/{blockId}")));
            await client.PostAsync($"/admin/content/{about}/publish", Form(("__RequestVerificationToken", token)));
            Assert.Contains("href=\"/about/jobs\"", await client.GetStringAsync("/about"));
            await client.PostAsync($"/admin/content/{jobs}", Form(("__RequestVerificationToken", token), ("title", "Jobs"), ("slug", "careers"),
                ("excerpt", ""), ("groupId", groupId), ("parentId", about)));
            var renamedLinks = await client.GetStringAsync("/about");
            Assert.Contains("href=\"/about/careers\"", renamedLinks);
            Assert.DoesNotContain("href=\"/about/jobs\"", renamedLinks);
            await client.PostAsync($"/admin/content/{jobs}/unpublish", Form(("__RequestVerificationToken", token)));
            Assert.DoesNotContain("/about/careers", await client.GetStringAsync("/about"));
            Assert.Contains("/about/careers", await client.GetStringAsync($"/admin/editor/content-{about}/canvas")); // the editor still shows it
            await client.PostAsync($"/admin/content/{jobs}/publish", Form(("__RequestVerificationToken", token)));
            // new layout options reach the page
            var lay = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{about}/ops")
            {
                Content = new StringContent("""{"op":"insert","type":"group","parentId":null,"index":0}""", System.Text.Encoding.UTF8, "application/json"),
            };
            lay.Headers.Add("RequestVerificationToken", token);
            lay.Headers.Add("Accept", "application/json");
            var groupId2 = Regex.Match(await (await client.SendAsync(lay)).Content.ReadAsStringAsync(), "\"newId\":\"([^\"]+)\"").Groups[1].Value;
            async Task<string> Insert(string type, string parent)
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{about}/ops")
                {
                    Content = new StringContent($$"""{"op":"insert","type":"{{type}}","parentId":"{{parent}}","index":0}""", System.Text.Encoding.UTF8, "application/json"),
                };
                r.Headers.Add("RequestVerificationToken", token);
                r.Headers.Add("Accept", "application/json");
                return Regex.Match(await (await client.SendAsync(r)).Content.ReadAsStringAsync(), "\"newId\":\"([^\"]+)\"").Groups[1].Value;
            }
            async Task SetProps(string id, params (string, string)[] f)
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"/admin/editor/content-{about}/props/{id}") { Content = Form(f) };
                r.Headers.Add("RequestVerificationToken", token);
                r.Headers.Add("HX-Request", "true");
                Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(r)).StatusCode);
            }
            var menuInGroup = await Insert("menu", groupId2);
            await SetProps(menuInGroup, ("p.MenuKey", "main"), ("p.Orientation", "Vertical"), ("p.Look", "Pills"), ("p.Collapse", "true"));
            await SetProps(groupId2, ("p.Direction", "Vertical"), ("p.Justify", "Center"), ("p.Background", "surface"));
            await client.PostAsync($"/admin/content/{about}/publish", Form(("__RequestVerificationToken", token)));
            var laidOut = await client.GetStringAsync("/about");
            Assert.Contains("grp grp-col justify-center", laidOut);
            Assert.Contains("bg-surface has-bg", laidOut);
            Assert.Contains("menu menu-vertical look-pills", laidOut);
            Assert.Contains("<details class=\"menu-fold\">", laidOut);

            // menus too
            await client.PostAsync("/admin/menus/footer", Form(("__RequestVerificationToken", token), ("name", "Footer menu"),
                ("icon", ""), ("label", "Careers"), ("url", $"page:{jobs}"), ("newTab", "false")));
            Assert.Contains("<a href=\"/about/careers\"><span>Careers</span></a>", await client.GetStringAsync("/blog"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEPAGE_ConnectionStrings__Default", null);
            Environment.SetEnvironmentVariable("HOMEPAGE_Paths__Data", null);
        }
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"(?:__RequestVerificationToken|csrf-token)\" (?:value|content)=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } t
            ? t : throw new InvalidOperationException("No antiforgery token in the page.");

    private static FormUrlEncodedContent Form(params (string, string)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
}

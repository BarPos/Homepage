using Homepage.Blocks;
using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Permissions;
using Homepage.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace Homepage.Tests;

[Collection("Postgres"), Trait("Category", "Postgres")]
public sealed class PostgresTests(PostgresFixture pg)
{
    private static readonly TimeProvider Time = TimeProvider.System;

    private async Task<string> MigratedDatabaseAsync()
    {
        var cs = await pg.CreateDatabaseAsync();
        await using var db = PostgresFixture.Context(cs);
        await db.Database.MigrateAsync();
        await Startup.MigrateContentAsync(db, ContentMigrator.Default);
        await Seeder.SeedAsync(db, Time);
        return cs;
    }

    [Fact]
    public async Task MigrationsMatchTheModelAndSeedIsIdempotent()
    {
        var cs = await MigratedDatabaseAsync();
        await using var db = PostgresFixture.Context(cs);
        Assert.False(db.Database.HasPendingModelChanges(), "The model has changes without a migration: run dotnet ef migrations add.");
        await Seeder.SeedAsync(db, Time);
        Assert.Equal(7, await db.Groups.CountAsync());
        Assert.Equal(4, await db.PermissionRoles.CountAsync());
        Assert.Equal(1, await db.Themes.CountAsync(t => t.IsActive));
        Assert.Equal(2, await db.ContentItems.CountAsync());
    }

    [Fact]
    public async Task ResolverCombinesDirectAndRoleGrantsAndIgnoresInvalid()
    {
        var cs = await MigratedDatabaseAsync();
        await using var db = PostgresFixture.Context(cs);
        var content = await db.Groups.SingleAsync(g => g.Name == SystemGroups.Content);
        var blog = new Group { Name = "blog", CreatedAtUtc = DateTime.UtcNow };
        db.Groups.Add(blog);
        var user = new User { UserName = "ann", NormalizedUserName = "ANN", DisplayName = "Ann", CreatedAtUtc = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var author = await db.PermissionRoles.SingleAsync(r => r.Name == "Author");
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = author.Id });
        db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = blog.Id, Permission = "publish" });
        await db.SaveChangesAsync();

        var p = await PermissionService.LoadAsync(db, user.Id, default);
        Assert.True(p.Has(content.Id, Perm.Create));      // via role
        Assert.True(p.Has(content.Id, Perm.Ro));
        Assert.False(p.Has(content.Id, Perm.Publish));
        Assert.True(p.Has(blog.Id, Perm.Publish));         // direct
        Assert.False(p.IsAdmin);

        author.Valid = false;
        blog.Valid = false;
        await db.SaveChangesAsync();
        p = await PermissionService.LoadAsync(db, user.Id, default);
        Assert.False(p.Has(content.Id, Perm.Ro));           // invalid role contributes nothing
        Assert.False(p.Has(blog.Id, Perm.Publish));         // invalid group contributes nothing

        author.Valid = true; blog.Valid = true; user.Valid = false;
        await db.SaveChangesAsync();
        Assert.False((await PermissionService.LoadAsync(db, user.Id, default)).CanSeeAdmin); // invalid user
    }

    [Fact]
    public async Task ListsNeverLeakItemsFromInvisibleGroups()
    {
        var cs = await MigratedDatabaseAsync();
        var factory = new TestDbFactory(cs);
        int blogId, contentId;
        await using (var db = PostgresFixture.Context(cs))
        {
            db.Groups.Add(new Group { Name = "secret", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            blogId = (await db.Groups.SingleAsync(g => g.Name == "secret")).Id;
            contentId = (await db.Groups.SingleAsync(g => g.Name == SystemGroups.Content)).Id;
        }
        var names = new Dictionary<string, int> { [SystemGroups.Admin] = 1, [SystemGroups.Content] = contentId, ["secret"] = blogId };
        var admin = new EffectivePermissions(0, [new Grant(1, Perm.Rw)], names);
        var author = new EffectivePermissions(0, [new Grant(contentId, Perm.Create), new Grant(contentId, Perm.Ro)], names);
        var svc = new ContentService(factory, new ContentExtractor(new MarkdownRenderer()), new NullAudit(), Time);

        var hidden = await svc.CreateAsync(admin, ContentType.Post, "Secret plans", blogId, "test");
        await svc.CreateAsync(author, ContentType.Post, "My draft", contentId, "test");

        var visible = (await svc.ListAsync(author, new ContentQuery())).Items;
        Assert.DoesNotContain(visible, c => c.Id == hidden.Id);
        Assert.Contains(visible, c => c.Title == "My draft");
        Assert.Empty((await svc.ListAsync(author, new ContentQuery(GroupId: blogId))).Items);
        Assert.Equal(0, (await svc.ListAsync(author, new ContentQuery(Search: "Secret"))).Total);
        Assert.Equal(2, (await svc.CountsAsync(author)).Posts); // "My draft" and the sample post
        await Assert.ThrowsAsync<NotFoundException>(() => svc.GetForViewAsync(author, hidden.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => svc.CreateAsync(author, ContentType.Page, "Nope", blogId, "test"));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.ApplyAsync(author, new EditorTarget(EditorTarget.ContentKind, hidden.Id), o => o.Insert("heading", null, 0), "test"));
    }

    [Fact]
    public async Task AuthorCanEditOwnDraftButNotPublish()
    {
        var cs = await MigratedDatabaseAsync();
        var factory = new TestDbFactory(cs);
        int contentId, authorId;
        await using (var db = PostgresFixture.Context(cs))
        {
            contentId = (await db.Groups.SingleAsync(g => g.Name == SystemGroups.Content)).Id;
            var u = new User { UserName = "writer", NormalizedUserName = "WRITER", CreatedAtUtc = DateTime.UtcNow };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            authorId = u.Id;
        }
        var names = new Dictionary<string, int> { [SystemGroups.Content] = contentId };
        var author = new EffectivePermissions(authorId, [new Grant(contentId, Perm.Create), new Grant(contentId, Perm.Ro)], names);
        var reviewer = new EffectivePermissions(0, [new Grant(contentId, Perm.Ro), new Grant(contentId, Perm.Publish)], names);
        var svc = new ContentService(factory, new ContentExtractor(new MarkdownRenderer()), new NullAudit(), Time);

        var post = await svc.CreateAsync(author, ContentType.Post, "Hello", contentId, "author");
        var target = new EditorTarget(EditorTarget.ContentKind, post.Id);
        var (tree, _, id) = await svc.ApplyAsync(author, target, o => o.Insert("heading", null, 0), "author");
        Assert.NotNull(tree.Find(id!));
        await Assert.ThrowsAsync<AccessDeniedException>(() => svc.PublishAsync(author, post.Id, "author"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => svc.ApplyAsync(reviewer, target, o => o.Insert("heading", null, 0), "reviewer"));
        var r = await svc.PublishAsync(reviewer, post.Id, "reviewer");
        Assert.True(r.AffectsSite);
        await using var check = PostgresFixture.Context(cs);
        var saved = await check.ContentItems.SingleAsync(c => c.Id == post.Id);
        Assert.Equal(ContentStatus.Published, saved.Status);
        Assert.Equal(1, await check.Revisions.CountAsync(x => x.ContentItemId == post.Id));
        // once published, the author's create permission no longer covers editing it
        await Assert.ThrowsAsync<AccessDeniedException>(() => svc.ApplyAsync(author, target, o => o.Insert("heading", null, 0), "author"));
    }

    [Fact]
    public async Task FullTextSearchFindsPublishedContent()
    {
        var cs = await MigratedDatabaseAsync();
        var factory = new TestDbFactory(cs);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new Homepage.Web.Rendering.PublicContent(factory, cache, new BlogService(factory, cache, new NullAudit(), Time));
        var hits = await svc.SearchAsync("welcome");
        Assert.Contains(hits, h => h.Slug == "home");
        Assert.Empty(await svc.SearchAsync("zebra"));
    }

    [Fact]
    public async Task BlogsMigrationKeepsExistingPostsReachable()
    {
        // A database at the first release: posts had no blog yet.
        var cs = await pg.CreateDatabaseAsync();
        await using (var db = PostgresFixture.Context(cs))
        {
            await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261122134906_Initial");
            // Plain Npgsql: EF would read the JSON braces as format placeholders.
            await using (var conn = new Npgsql.NpgsqlConnection(cs))
            {
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO groups ("Name","Comment","IsSystem","Valid","CreatedAtUtc") VALUES ('content','',false,true,now());
                INSERT INTO content_items ("Type","GroupId","Slug","Title","Excerpt","DraftBlocks","Blocks","Status","EverPublished","SearchText","CreatedAtUtc","UpdatedAtUtc","PublishedAtUtc")
                SELECT 1, "Id", 'old-post', 'Old post', '', '{"schemaVersion":1,"blocks":[]}', '{"schemaVersion":1,"blocks":[]}', 1, true, '', now(), now(), now() FROM groups WHERE "Name" = 'content';
                INSERT INTO themes ("Name","Slug","IsBuiltIn","IsActive","ThemeVersion","SchemaVersion","Tokens","CustomCss","HeaderBlocks","FooterBlocks","UpdatedAtUtc")
                VALUES ('Mine','mine',false,true,1,1,'{}','','{"schemaVersion":1,"blocks":[{"id":"h1","type":"section","props":{},"children":[{"id":"h2","type":"siteheader","props":{"menuKey":"main","showTagline":true},"children":[]}]}]}','{"schemaVersion":1,"blocks":[]}',now());
                """, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await db.Database.MigrateAsync();
            await Startup.MigrateContentAsync(db, ContentMigrator.Default);
        }
        await using var check = PostgresFixture.Context(cs);
        var blog = await check.Blogs.SingleAsync();
        Assert.Equal("blog", blog.Slug);
        var post = await check.ContentItems.SingleAsync(c => c.Slug == "old-post");
        Assert.Equal(blog.Id, post.BlogId);
        Assert.Equal(2, BlockTree.Parse(post.Blocks).SchemaVersion);
        // the nested v1 header became an editable site bar
        var header = BlockTree.Parse((await check.Themes.SingleAsync(t => t.Slug == "mine")).HeaderBlocks);
        var bar = header.Blocks[0].Children[0];
        Assert.Equal("sitebar", bar.Type);
        Assert.Equal(["sitebrand", "menu"], bar.Children.Select(c => c.Type));
        Assert.True(bar.Children[0].PropsAs<SiteBrandProps>().ShowTagline);
        Assert.Empty(BlockValidator.Validate(header, TreeKind.Header));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var factory = new TestDbFactory(cs);
        var pub = new Homepage.Web.Rendering.PublicContent(factory, cache, new BlogService(factory, cache, new NullAudit(), Time));
        Assert.NotNull(await pub.GetPublishedPostAsync(blog.Id, "old-post"));
        Assert.Equal("/blog/old-post", (await pub.LatestPostsAsync(5)).Single().Url);
    }

    [Fact]
    public async Task BlogAddressesArePagesAddressesTooAndRenamesRedirect()
    {
        var cs = await MigratedDatabaseAsync();
        var factory = new TestDbFactory(cs);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var blogs = new BlogService(factory, cache, new NullAudit(), Time);
        int adminId, contentId;
        await using (var db = PostgresFixture.Context(cs))
        {
            adminId = (await db.Groups.SingleAsync(g => g.Name == SystemGroups.Admin)).Id;
            contentId = (await db.Groups.SingleAsync(g => g.Name == SystemGroups.Content)).Id;
        }
        var names = new Dictionary<string, int> { [SystemGroups.Admin] = adminId, [SystemGroups.Content] = contentId };
        var admin = new EffectivePermissions(0, [new Grant(adminId, Perm.Rw)], names);
        var author = new EffectivePermissions(0, [new Grant(contentId, Perm.Rw)], names);
        var svc = new ContentService(factory, new ContentExtractor(new MarkdownRenderer()), new NullAudit(), Time);

        await Assert.ThrowsAsync<AccessDeniedException>(() => blogs.SaveAsync(author, null, "Releases", "releases", "", 10, "a"));
        var releases = await blogs.SaveAsync(admin, null, "Releases", "releases", "Version notes", 10, "a");
        await Assert.ThrowsAsync<ConflictException>(() => blogs.SaveAsync(admin, null, "Again", "releases", "", 10, "a"));
        await Assert.ThrowsAsync<ConflictException>(() => blogs.SaveAsync(admin, null, "Bad", "admin", "", 10, "a"));
        await Assert.ThrowsAsync<ConflictException>(() => blogs.SaveAsync(admin, null, "Home", "home", "", 10, "a")); // the sample page

        // a page cannot take a blog's address, and a post's address is unique only within its blog
        var page = await svc.CreateAsync(admin, ContentType.Page, "Releases", contentId, "a");
        Assert.Equal("releases-2", page.Slug);
        await Assert.ThrowsAsync<ConflictException>(() => svc.UpdateSettingsAsync(admin, page.Id, "Releases", "releases", "", contentId, "a"));
        var a = await svc.CreateAsync(admin, ContentType.Post, "Version 1.0", contentId, "a", releases.Id);
        var defaultBlog = (await blogs.AllAsync()).First(b => b.Slug == "blog");
        var b2 = await svc.CreateAsync(admin, ContentType.Post, "Version 1.0", contentId, "a", defaultBlog.Id);
        Assert.Equal(a.Slug, b2.Slug);
        Assert.Equal(releases.Id, a.BlogId);

        // renaming keeps the old address as a redirect
        await blogs.SaveAsync(admin, releases.Id, "Releases", "changelog", "", 10, "a");
        Assert.Equal("changelog", (await blogs.ByPreviousSlugAsync("releases"))!.Slug);
        await Assert.ThrowsAsync<ConflictException>(() => blogs.DeleteAsync(admin, releases.Id, "a"));
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task WriteAsync(string actor, string action, string entity, string entityId, string details, string? ip = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}

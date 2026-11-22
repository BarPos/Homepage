using Homepage.Blocks;
using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Permissions;
using Homepage.Server;
using Microsoft.EntityFrameworkCore;
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

        var visible = await svc.ListAsync(author, null, null, null);
        Assert.DoesNotContain(visible, c => c.Id == hidden.Id);
        Assert.Contains(visible, c => c.Title == "My draft");
        Assert.Empty(await svc.ListAsync(author, null, blogId, null));
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
        var svc = new Homepage.Web.Rendering.PublicContent(factory, new MemoryCache(new MemoryCacheOptions()));
        var hits = await svc.SearchAsync("welcome");
        Assert.Contains(hits, h => h.Slug == "home");
        Assert.Empty(await svc.SearchAsync("zebra"));
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task WriteAsync(string actor, string action, string entity, string entityId, string details, string? ip = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}

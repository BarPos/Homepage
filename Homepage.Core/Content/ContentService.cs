using Homepage.Blocks;
using Homepage.Common;
using Homepage.Data;
using Homepage.Permissions;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Content;

public sealed class AccessDeniedException(string message = "You do not have permission to do that.") : Exception(message);
public sealed class NotFoundException(string message = "Not found.") : Exception(message);
public sealed class ConflictException(string message) : Exception(message);

/// <summary>Result of a change; <see cref="AffectsSite"/> tells the web layer to evict the public output cache.</summary>
public sealed record ChangeResult(bool AffectsSite);

/// <summary>
/// Content and editor operations with OTRS-style permission checks. Every method checks on the server;
/// the UI only hides what the user cannot do.
/// </summary>
public sealed class ContentService(
    IDbContextFactory<AppDbContext> dbFactory,
    ContentExtractor extractor,
    IAuditLog audit,
    TimeProvider time)
{
    public static ContentFacts Facts(ContentItem c) => new(c.GroupId, c.AuthorId, c.EverPublished);

    /// <summary>Page addresses taken by fixed routes; a page there would publish but never be reachable.</summary>
    public static readonly IReadOnlySet<string> ReservedPageSlugs = new HashSet<string>(StringComparer.Ordinal)
        { "admin", "blog", "search", "media", "theme", "healthz", "feed-xml", "sitemap-xml", "robots-txt" };

    private static bool IsReserved(ContentType type, string slug) => type == ContentType.Page && ReservedPageSlugs.Contains(slug);

    // ---------------------------------------------------------------- listing

    /// <summary>Items the user can see (ro on their group), filtered in SQL.</summary>
    public async Task<List<ContentItem>> ListAsync(EffectivePermissions perms, ContentType? type, int? groupId, string? search,
        int take = 200, CancellationToken ct = default)
    {
        var visible = perms.GroupsWith(Perm.Ro);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.ContentItems.AsNoTracking().Where(c => visible.Contains(c.GroupId));
        if (type is { } t) q = q.Where(c => c.Type == t);
        if (groupId is { } g) q = q.Where(c => c.GroupId == g);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c => EF.Functions.ILike(c.Title, "%" + s.Replace("%", "\\%").Replace("_", "\\_") + "%"));
        }
        return await q.OrderByDescending(c => c.UpdatedAtUtc).Take(take).ToListAsync(ct);
    }

    public async Task<ContentItem> GetForViewAsync(EffectivePermissions perms, int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ContentItems.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        if (!ContentRules.CanView(perms, Facts(item))) throw new NotFoundException();
        return item;
    }

    // ---------------------------------------------------------------- create / settings / publish

    public async Task<ContentItem> CreateAsync(EffectivePermissions perms, ContentType type, string title, int groupId, string actor,
        CancellationToken ct = default)
    {
        if (!ContentRules.CanCreateIn(perms, groupId)) throw new AccessDeniedException("You cannot create items in that group.");
        title = title.Trim();
        if (title.Length is 0 or > 300) throw new BlockException("The title must be 1 to 300 characters.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Groups.AnyAsync(g => g.Id == groupId && g.Valid, ct)) throw new NotFoundException("The group does not exist.");
        var slug = await UniqueSlugAsync(db, type, Slug.From(title) is { Length: > 0 } s ? s : "untitled", null, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var tree = type == ContentType.Post
            ? new BlockTree { Blocks = [new BlockNode { Type = "markdown", Props = BlockOps.ToJson(new MarkdownProps { Markdown = "" }) }] }
            : new BlockTree
            {
                Blocks =
                [
                    new BlockNode
                    {
                        Type = "section", Props = BlockOps.ToJson(new SectionProps()),
                        Children = [new BlockNode { Type = "heading", Props = BlockOps.ToJson(new HeadingProps { Text = title, Level = 1 }) }],
                    },
                ],
            };
        var item = new ContentItem
        {
            Type = type, GroupId = groupId, Title = title, Slug = slug, DraftBlocks = tree.ToJson(),
            AuthorId = perms.UserId == 0 ? null : perms.UserId, Status = ContentStatus.Draft,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.ContentItems.Add(item);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "content.create", type.ToString(), item.Id.ToString(), title, ct: ct);
        return item;
    }

    public async Task<ChangeResult> UpdateSettingsAsync(EffectivePermissions perms, int id, string title, string slug, string excerpt,
        int groupId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        var facts = Facts(item);
        if (!ContentRules.CanView(perms, facts)) throw new NotFoundException();
        if (!ContentRules.CanEdit(perms, facts)) throw new AccessDeniedException();
        title = title.Trim();
        if (title.Length is 0 or > 300) throw new BlockException("The title must be 1 to 300 characters.");
        slug = Slug.From(slug);
        if (slug.Length == 0) throw new BlockException("The slug must contain letters or digits.");
        if (IsReserved(item.Type, slug)) throw new ConflictException($"The address '/{slug}' is used by the site itself. Choose another one.");
        if (slug != item.Slug && await db.ContentItems.AnyAsync(c => c.Type == item.Type && c.Slug == slug && c.Id != id, ct))
            throw new ConflictException($"The address '{slug}' is already used.");
        if (groupId != item.GroupId)
        {
            if (!ContentRules.CanMove(perms, facts, groupId)) throw new AccessDeniedException("You cannot move this item to that group.");
            if (!await db.Groups.AnyAsync(g => g.Id == groupId && g.Valid, ct)) throw new NotFoundException("The group does not exist.");
        }
        var publicChange = item.Status == ContentStatus.Published && (item.Title != title || item.Slug != slug || item.Excerpt != excerpt.Trim());
        var moved = item.GroupId != groupId;
        item.Title = title;
        item.Slug = slug;
        item.Excerpt = excerpt.Trim().Length > 1000 ? excerpt.Trim()[..1000] : excerpt.Trim();
        item.GroupId = groupId;
        item.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await SaveAsync(db, ct);
        await audit.WriteAsync(actor, moved ? "content.move" : "content.settings", item.Type.ToString(), id.ToString(),
            moved ? $"{title} → group {groupId}" : title, ct: ct);
        return new ChangeResult(publicChange);
    }

    public async Task<ChangeResult> PublishAsync(EffectivePermissions perms, int id, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        if (!ContentRules.CanView(perms, Facts(item))) throw new NotFoundException();
        if (!ContentRules.CanPublish(perms, Facts(item))) throw new AccessDeniedException("You cannot publish in this group.");
        var tree = BlockTree.Parse(item.DraftBlocks);
        var errors = BlockValidator.Validate(tree, item.Type == ContentType.Post ? TreeKind.Post : TreeKind.Page);
        if (errors.Count > 0) throw new BlockException(string.Join(" ", errors));
        var now = time.GetUtcNow().UtcDateTime;
        item.Blocks = item.DraftBlocks;
        item.Status = ContentStatus.Published;
        item.EverPublished = true;
        item.PublishedAtUtc ??= now;
        item.UpdatedAtUtc = now;
        item.SearchText = extractor.ExtractText(tree);
        if (string.IsNullOrWhiteSpace(item.Excerpt)) item.Excerpt = ContentExtractor.Excerpt(item.SearchText);
        db.Revisions.Add(new Revision
        {
            ContentItemId = id, Title = item.Title, Blocks = item.DraftBlocks, SchemaVersion = tree.SchemaVersion,
            AuthorId = perms.UserId == 0 ? null : perms.UserId, CreatedAtUtc = now,
        });
        await SaveAsync(db, ct);
        await audit.WriteAsync(actor, "content.publish", item.Type.ToString(), id.ToString(), item.Title, ct: ct);
        return new ChangeResult(true);
    }

    public async Task<ChangeResult> UnpublishAsync(EffectivePermissions perms, int id, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        if (!ContentRules.CanView(perms, Facts(item))) throw new NotFoundException();
        if (!ContentRules.CanPublish(perms, Facts(item))) throw new AccessDeniedException("You cannot unpublish in this group.");
        item.Status = ContentStatus.Draft;
        item.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await SaveAsync(db, ct);
        await audit.WriteAsync(actor, "content.unpublish", item.Type.ToString(), id.ToString(), item.Title, ct: ct);
        return new ChangeResult(true);
    }

    public async Task<ChangeResult> DeleteAsync(EffectivePermissions perms, int id, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        if (!ContentRules.CanView(perms, Facts(item))) throw new NotFoundException();
        if (!ContentRules.CanDelete(perms, Facts(item))) throw new AccessDeniedException("You cannot delete this item.");
        var wasPublic = item.Status == ContentStatus.Published;
        db.ContentItems.Remove(item);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "content.delete", item.Type.ToString(), id.ToString(), item.Title, ct: ct);
        return new ChangeResult(wasPublic);
    }

    // ---------------------------------------------------------------- visual editor

    public async Task<EditorDocument> LoadDocumentAsync(EffectivePermissions perms, EditorTarget target, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var activeTheme = await db.Themes.AsNoTracking().Where(t => t.IsActive).Select(t => t.Id).FirstOrDefaultAsync(ct);
        if (target.Kind == EditorTarget.ContentKind)
        {
            var item = await db.ContentItems.AsNoTracking().FirstOrDefaultAsync(c => c.Id == target.Id, ct) ?? throw new NotFoundException();
            var f = Facts(item);
            if (!ContentRules.CanView(perms, f)) throw new NotFoundException();
            return new EditorDocument(target, item.Title, BlockTree.Parse(item.DraftBlocks),
                item.Type == ContentType.Post ? TreeKind.Post : TreeKind.Page,
                ContentRules.CanEdit(perms, f), ContentRules.CanPublish(perms, f), item.GroupId, activeTheme);
        }
        if (!perms.HasSystem(SystemGroups.Layout, Perm.Ro) && !perms.HasSystem(SystemGroups.Themes, Perm.Ro)) throw new NotFoundException();
        var theme = await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == target.Id, ct) ?? throw new NotFoundException();
        var header = target.Kind == EditorTarget.HeaderKind;
        return new EditorDocument(target, $"{theme.Name}: {(header ? "header" : "footer")}",
            BlockTree.Parse(header ? theme.HeaderBlocks : theme.FooterBlocks),
            header ? TreeKind.Header : TreeKind.Footer,
            !theme.IsBuiltIn && perms.HasSystem(SystemGroups.Layout, Perm.Rw), false, null, theme.Id);
    }

    /// <summary>Applies one editor operation to the draft (or theme layout) and saves it. Returns the new tree.</summary>
    public async Task<(BlockTree Tree, ChangeResult Change, string? NewId)> ApplyAsync(EffectivePermissions perms, EditorTarget target,
        Func<BlockOps, string?> op, string actor, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            try
            {
                if (target.Kind == EditorTarget.ContentKind)
                {
                    var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == target.Id, ct) ?? throw new NotFoundException();
                    var f = Facts(item);
                    if (!ContentRules.CanView(perms, f)) throw new NotFoundException();
                    if (!ContentRules.CanEdit(perms, f)) throw new AccessDeniedException("You cannot edit this item.");
                    var kind = item.Type == ContentType.Post ? TreeKind.Post : TreeKind.Page;
                    var ops = new BlockOps(BlockTree.Parse(item.DraftBlocks), kind, perms);
                    var newId = op(ops);
                    Check(ops.Tree, kind);
                    item.DraftBlocks = ops.Tree.ToJson();
                    item.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(ct);
                    return (ops.Tree, new ChangeResult(false), newId);
                }
                else
                {
                    if (!perms.HasSystem(SystemGroups.Layout, Perm.Rw)) throw new AccessDeniedException("Editing the header and footer needs layout: rw.");
                    var theme = await db.Themes.FirstOrDefaultAsync(t => t.Id == target.Id, ct) ?? throw new NotFoundException();
                    if (theme.IsBuiltIn) throw new AccessDeniedException("Built-in themes are read-only. Duplicate the theme to change it.");
                    var header = target.Kind == EditorTarget.HeaderKind;
                    var kind = header ? TreeKind.Header : TreeKind.Footer;
                    var ops = new BlockOps(BlockTree.Parse(header ? theme.HeaderBlocks : theme.FooterBlocks), kind, perms);
                    var newId = op(ops);
                    Check(ops.Tree, kind);
                    if (header) theme.HeaderBlocks = ops.Tree.ToJson(); else theme.FooterBlocks = ops.Tree.ToJson();
                    theme.ThemeVersion++;
                    theme.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
                    await db.SaveChangesAsync(ct);
                    await audit.WriteAsync(actor, "theme.layout", "Theme", theme.Id.ToString(), header ? "header" : "footer", ct: ct);
                    return (ops.Tree, new ChangeResult(theme.IsActive), newId);
                }
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                // Someone saved in between: re-apply the operation on their version once.
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("Someone else saved this page at the same time. Reload the editor.");
            }
        }
    }

    private static void Check(BlockTree tree, TreeKind kind)
    {
        var errors = BlockValidator.Validate(tree, kind);
        if (errors.Count > 0) throw new BlockException(string.Join(" ", errors));
    }

    private static async Task SaveAsync(AppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Someone else changed this item. Reload and try again."); }
    }

    private static async Task<string> UniqueSlugAsync(AppDbContext db, ContentType type, string slug, int? exceptId, CancellationToken ct)
    {
        var candidate = slug;
        for (var i = 2; IsReserved(type, candidate) || await db.ContentItems.AnyAsync(c => c.Type == type && c.Slug == candidate && c.Id != exceptId, ct); i++)
            candidate = $"{slug}-{i}";
        return candidate;
    }
}

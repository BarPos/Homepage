using Homepage.Blocks;
using Homepage.Common;
using Homepage.Permissions;
using Homepage.Themes;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Data;

/// <summary>
/// Idempotent first-run data, applied after migrations (at start and by --migrate-only):
/// system groups, the content group, default roles, built-in themes, menus, settings and a sample home page.
/// Existing rows are never changed, so users can edit roles and settings freely.
/// </summary>
public static class Seeder
{
    public static async Task SeedAsync(AppDbContext db, TimeProvider time, CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var changed = false;

        foreach (var (name, comment) in SystemGroups.All)
            if (!await db.Groups.AnyAsync(g => g.Name == name, ct))
            {
                db.Groups.Add(new Group { Name = name, Comment = comment, IsSystem = true, CreatedAtUtc = now });
                changed = true;
            }
        var firstRun = !await db.Groups.AnyAsync(g => !g.IsSystem, ct) && !await db.ContentItems.AnyAsync(ct);
        if (firstRun && !await db.Groups.AnyAsync(g => g.Name == SystemGroups.Content, ct))
        {
            db.Groups.Add(new Group { Name = SystemGroups.Content, Comment = "Default group for pages, posts and media.", CreatedAtUtc = now });
            changed = true;
        }
        if (changed) { await db.SaveChangesAsync(ct); changed = false; }

        var groupId = await db.Groups.ToDictionaryAsync(g => g.Name, g => g.Id, ct);

        if (!await db.PermissionRoles.AnyAsync(ct) && groupId.TryGetValue(SystemGroups.Content, out var contentId))
        {
            async Task AddRole(string name, string comment, params (string Group, Perm Perm)[] grants)
            {
                var role = new Role { Name = name, Comment = comment, CreatedAtUtc = now };
                db.PermissionRoles.Add(role);
                await db.SaveChangesAsync(ct);
                foreach (var (g, p) in grants)
                    db.RoleGroups.Add(new RoleGroup { RoleId = role.Id, GroupId = groupId[g], Permission = PermNames.ToKey(p) });
            }
            await AddRole("Administrator", "Full access.", (SystemGroups.Admin, Perm.Rw));
            await AddRole("Editor", "All content, layout and menus; can view themes.",
                (SystemGroups.Content, Perm.Rw), (SystemGroups.Layout, Perm.Rw), (SystemGroups.Themes, Perm.Ro));
            await AddRole("Author", "Writes and edits own drafts.", (SystemGroups.Content, Perm.Create), (SystemGroups.Content, Perm.Ro));
            await AddRole("Reviewer", "Reads and publishes content.", (SystemGroups.Content, Perm.Ro), (SystemGroups.Content, Perm.Publish));
            changed = true;
            _ = contentId;
        }

        var themeOrder = 0;
        foreach (var def in BuiltInThemes.All)
        {
            var existing = await db.Themes.FirstOrDefaultAsync(t => t.Slug == def.Slug && t.IsBuiltIn, ct);
            if (existing is not null)
            {
                // Built-in themes are read-only, so a new release may restyle them: take its tokens and bump the CSS version.
                var tokens = def.Tokens.ToJson();
                if (ThemeTokens.Parse(existing.Tokens).ToJson() != tokens || existing.Name != def.Name) // jsonb reorders keys: compare normalized
                {
                    existing.Tokens = tokens;
                    existing.Name = def.Name;
                    existing.ThemeVersion++;
                    existing.UpdatedAtUtc = now;
                    changed = true;
                }
                themeOrder++;
                continue;
            }
            if (await db.Themes.AnyAsync(t => t.Slug == def.Slug, ct)) { themeOrder++; continue; }
            db.Themes.Add(new Theme
            {
                Name = def.Name, Slug = def.Slug, IsBuiltIn = true,
                IsActive = themeOrder == 0 && !await db.Themes.AnyAsync(t => t.IsActive, ct),
                Tokens = def.Tokens.ToJson(),
                HeaderBlocks = BuiltInThemes.DefaultHeader().ToJson(),
                FooterBlocks = BuiltInThemes.DefaultFooter().ToJson(),
                UpdatedAtUtc = now,
            });
            themeOrder++;
            changed = true;
        }

        if (!await db.Menus.AnyAsync(m => m.Key == "main", ct))
        {
            db.Menus.Add(new Menu { Key = "main", Name = "Main menu", Items = """[{"label":"Home","url":"/"},{"label":"Blog","url":"/blog"}]""" });
            changed = true;
        }
        if (!await db.Menus.AnyAsync(m => m.Key == "footer", ct))
        {
            db.Menus.Add(new Menu { Key = "footer", Name = "Footer menu", Items = """[{"label":"Blog","url":"/blog"},{"label":"RSS","url":"/feed.xml"}]""" });
            changed = true;
        }
        if (changed) { await db.SaveChangesAsync(ct); changed = false; }

        if (!await db.Settings.AnyAsync(s => s.Key == SiteSettings.TitleKey, ct))
        {
            db.Settings.Add(new Setting { Key = SiteSettings.TitleKey, Value = "My website" });
            db.Settings.Add(new Setting { Key = SiteSettings.TaglineKey, Value = "Built with Homepage" });
            if (groupId.TryGetValue(SystemGroups.Content, out var cg))
                db.Settings.Add(new Setting { Key = SiteSettings.DefaultGroupKey, Value = cg.ToString() });
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);

        if (firstRun && groupId.TryGetValue(SystemGroups.Content, out var homeGroup))
        {
            var home = SampleHome();
            var json = home.ToJson();
            var item = new ContentItem
            {
                Type = ContentType.Page, GroupId = homeGroup, Slug = "home", Title = "Home",
                DraftBlocks = json, Blocks = json, Status = ContentStatus.Published, EverPublished = true,
                PublishedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
                SearchText = "Your site is up and running. Welcome home page",
            };
            db.ContentItems.Add(item);
            await db.SaveChangesAsync(ct);
            db.Settings.Add(new Setting { Key = SiteSettings.HomePageKey, Value = item.Id.ToString() });
            var post = SamplePost().ToJson();
            var blogId = await db.Blogs.OrderBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync(ct);
            db.ContentItems.Add(new ContentItem
            {
                Type = ContentType.Post, GroupId = homeGroup, BlogId = blogId, Slug = "hello-world", Title = "Hello, world",
                Excerpt = "A first post to show how writing works here: Markdown with other blocks in between.",
                DraftBlocks = post, Blocks = post, Status = ContentStatus.Published, EverPublished = true,
                PublishedAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
                SearchText = "Hello world Markdown blocks quote publish",
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private static BlockNode Node<T>(string type, T props, params BlockNode[] children) where T : notnull =>
        new() { Type = type, Props = BlockOps.ToJson(props), Children = [.. children] };

    private static BlockNode Md(string text) => Node("markdown", new MarkdownProps { Markdown = text });

    private static BlockTree SampleHome() => new()
    {
        Blocks =
        [
            Node("section", new SectionProps { Variant = SectionVariant.Hero, Background = "bg", Padding = Spacing.Xl },
                Node("heading", new HeadingProps { Text = "Your site is up and running.", Level = 1 }),
                Md("This is the home page. Sign in at **/admin** to change it: drag blocks around, pick a theme, or start writing."),
                Node("button", new ButtonProps { Text = "Read the blog", Url = "/blog" })),
            Node("section", new SectionProps { Variant = SectionVariant.Glass, Background = "bg", Padding = Spacing.Md },
                Node("columns", new ColumnsProps { Gap = Spacing.Lg },
                    Node("column", new ColumnProps(),
                        Node("heading", new HeadingProps { Text = "Built from blocks", Level = 3 }),
                        Md("Sections, columns, text, images and buttons. Arrange them in the editor and see the real page as you go.")),
                    Node("column", new ColumnProps(),
                        Node("heading", new HeadingProps { Text = "One look, everywhere", Level = 3 }),
                        Md("Colors, type and spacing live in the theme. Change it once and every page follows.")),
                    Node("column", new ColumnProps(),
                        Node("heading", new HeadingProps { Text = "Write in Markdown", Level = 3 }),
                        Md("Posts start as plain text. Drop an image or a quote in between paragraphs whenever you need one.")))),
            Node("section", new SectionProps { Background = "bg", Padding = Spacing.Lg },
                Node("heading", new HeadingProps { Text = "From the blog", Level = 2 }),
                Node("postlist", new PostListProps { Count = 3, Layout = PostListLayout.Grid })),
        ],
    };

    private static BlockTree SamplePost() => new()
    {
        Blocks =
        [
            Md("""
               Every post is made of blocks, and this one is mostly a single **Markdown block**. Write the way you would in any text editor:
               headings, lists, links and `code` all work.

               ## Mixing in other blocks

               Below this paragraph sits a quote block. In the editor, select any block and use the panel on the left to add an image,
               a button or a divider right where you need it.
               """),
            Node("quote", new QuoteProps { Text = "The best way to get started is to start.", Cite = "Every writing teacher, probably" }),
            Md("""
               When you are happy with it, press **Publish**. Until then, nobody but you and your editors can see the draft.

               You can delete this post whenever you like.
               """),
        ],
    };
}

using Microsoft.AspNetCore.Identity;

namespace Homepage.Data;

/// <summary>An admin-area account (OTRS: agent). ASP.NET Core Identity handles password, lockout and 2FA.</summary>
public sealed class User : IdentityUser<int>
{
    public string DisplayName { get; set; } = "";
    /// <summary>Invalid users cannot sign in and contribute no permissions (OTRS valid_id).</summary>
    public bool Valid { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool IsSystem { get; set; }
    public bool Valid { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool Valid { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>User ↔ group grant, one row per permission type (OTRS group_user).</summary>
public sealed class UserGroup
{
    public int UserId { get; set; }
    public int GroupId { get; set; }
    public string Permission { get; set; } = "";
}

/// <summary>Role ↔ group grant (OTRS group_role).</summary>
public sealed class RoleGroup
{
    public int RoleId { get; set; }
    public int GroupId { get; set; }
    public string Permission { get; set; } = "";
}

/// <summary>User ↔ role (OTRS role_user).</summary>
public sealed class UserRole
{
    public int UserId { get; set; }
    public int RoleId { get; set; }
}

public enum ContentType { Page = 0, Post = 1 }

/// <summary>A blog: a list of posts at its own address (/blog, /releases, …), with its own RSS feed.</summary>
public sealed class Blog
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The last URL segment: /{parent page path}/{Slug} lists the posts, …/{Slug}/{post} shows one.</summary>
    public string Slug { get; set; } = "";
    /// <summary>The page the blog lives under (its address starts with that page's address); null = top level.</summary>
    public int? ParentPageId { get; set; }
    public string Description { get; set; } = "";
    public int PostsPerPage { get; set; } = 10;
    /// <summary>Earlier addresses (paths without the leading slash); requests to them are redirected (301) to the current one.</summary>
    public List<string> PreviousSlugs { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
}
public enum ContentStatus { Draft = 0, Published = 1 }

/// <summary>Which parts of the site frame a page shows.</summary>
public enum PageLayout { Full = 0, NoHeader = 1, NoFooter = 2, Blank = 3 }

/// <summary>A page or a blog post. Content is a block tree (JSON, see Homepage.Blocks).</summary>
public sealed class ContentItem
{
    public int Id { get; set; }
    public ContentType Type { get; set; }
    public int GroupId { get; set; }
    /// <summary>Pages only: the page this one is under (/parent/child); null = top level.</summary>
    public int? ParentId { get; set; }
    /// <summary>Posts only: the blog the post belongs to.</summary>
    public int? BlogId { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Excerpt { get; set; } = "";
    /// <summary>Published block tree (jsonb); null until first publish.</summary>
    public string? Blocks { get; set; }
    /// <summary>Autosaved draft block tree (jsonb), what the editor edits.</summary>
    public string DraftBlocks { get; set; } = "";
    public ContentStatus Status { get; set; }
    public bool EverPublished { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public int? AuthorId { get; set; }
    // Appearance options. A subpage (and a post or post list of a blog under a page) takes them from the page above
    // unless it sets its own; a top-level page falls back to the site's defaults.
    /// <summary>True: the theme of the page above (top level: the site's theme). False: <see cref="ThemeId"/>.</summary>
    public bool InheritTheme { get; set; } = true;
    /// <summary>With <see cref="InheritTheme"/> off: a theme for this item; null = the site's active theme.</summary>
    public int? ThemeId { get; set; }
    /// <summary>Null = the layout of the page above (top level: header and footer).</summary>
    public PageLayout? Layout { get; set; }
    /// <summary>Extra CSS for this item, added after the CSS of the pages above it (sanitized like a theme's custom CSS).</summary>
    public string CustomCss { get; set; } = "";
    /// <summary>Ask search engines not to index it (robots meta tag, left out of the sitemap). Null = like the page above (top level: index).</summary>
    public bool? NoIndex { get; set; }
    /// <summary>Text extracted from the published blocks, for full-text search.</summary>
    public string SearchText { get; set; } = "";
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    /// <summary>PostgreSQL xmin, for optimistic concurrency.</summary>
    public uint Version { get; set; }
}

public sealed class Revision
{
    public int Id { get; set; }
    public int ContentItemId { get; set; }
    public string Title { get; set; } = "";
    public string Blocks { get; set; } = "";
    public int SchemaVersion { get; set; }
    public int? AuthorId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class MediaItem
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>Random name on disk in Paths:Data/media/.</summary>
    public string StoredName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string AltText { get; set; } = "";
    public int? UploadedById { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A navigation menu. Items are JSON: [{ "label": "...", "url": "..." }].</summary>
public sealed class Menu
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Items { get; set; } = "[]";
}

/// <summary>
/// A theme: design tokens, custom CSS, and the site header and footer as block trees.
/// Every saved change increments <see cref="ThemeVersion"/>, which is part of the CSS URL.
/// </summary>
public sealed class Theme
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public bool IsActive { get; set; }
    public int ThemeVersion { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;
    public string Tokens { get; set; } = "{}";
    public string CustomCss { get; set; } = "";
    public string HeaderBlocks { get; set; } = "";
    public string FooterBlocks { get; set; } = "";
    // Unpublished changes. Null = no draft for that part; the editors work on draft ?? live, Publish copies to live.
    public string? DraftTokens { get; set; }
    public string? DraftCustomCss { get; set; }
    public string? DraftHeaderBlocks { get; set; }
    public string? DraftFooterBlocks { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public uint Version { get; set; }

    public bool HasDraft => DraftTokens is not null || DraftCustomCss is not null || DraftHeaderBlocks is not null || DraftFooterBlocks is not null;
}

public sealed class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Details { get; set; } = "";
    /// <summary>Client address of the request that caused the entry.</summary>
    public string? Ip { get; set; }
    /// <summary>Address the acting user signed in from (their session), when known.</summary>
    public string? SignInIp { get; set; }
}

public enum UpdateState { Downloading = 0, Staged = 1, Applying = 2, Succeeded = 3, RolledBack = 4, Failed = 5 }

public sealed class UpdateRun
{
    public Guid Id { get; set; }
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public UpdateState State { get; set; }
    public string? Message { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

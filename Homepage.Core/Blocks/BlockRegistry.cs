using Homepage.Permissions;

namespace Homepage.Blocks;

public enum BlockCategory { Layout, Content, Dynamic, Site }

/// <summary>Where a block tree lives; the palette and validation depend on it.</summary>
public enum TreeKind { Page, Post, Header, Footer }

/// <summary>One block type. Renderers live in Homepage.Web (one Razor component per type).</summary>
public sealed record BlockDefinition(
    string Type,
    string Label,
    BlockCategory Category,
    Type PropsType,
    string Icon,
    bool IsContainer = false,
    IReadOnlyList<string>? AllowedChildren = null,
    IReadOnlyList<string>? AllowedParents = null,
    string? RequiredSystemGroup = null,
    IReadOnlyList<TreeKind>? OnlyIn = null)
{
    public bool AllowsChild(string type) => IsContainer && (AllowedChildren is null || AllowedChildren.Contains(type));
    public bool AllowedUnder(string? parentType) => AllowedParents is null || AllowedParents.Contains(parentType ?? "");
    public bool AvailableIn(TreeKind kind) => OnlyIn is null || OnlyIn.Contains(kind);

    public object CreateDefaultProps() => Activator.CreateInstance(PropsType)!;
}

public static class BlockRegistry
{
    private static readonly string[] Content =
        ["heading", "markdown", "image", "button", "quote", "spacer", "divider", "html", "postlist", "menu"];

    public static readonly IReadOnlyList<BlockDefinition> All =
    [
        new("section", "Section", BlockCategory.Layout, typeof(SectionProps), "▭", IsContainer: true,
            AllowedChildren: [.. Content, "columns", "siteheader"], AllowedParents: [""]),
        new("columns", "Columns", BlockCategory.Layout, typeof(ColumnsProps), "▥", IsContainer: true,
            AllowedChildren: ["column"]),
        new("column", "Column", BlockCategory.Layout, typeof(ColumnProps), "▯", IsContainer: true,
            AllowedChildren: Content, AllowedParents: ["columns"]),
        new("heading", "Heading", BlockCategory.Content, typeof(HeadingProps), "H"),
        new("markdown", "Text (Markdown)", BlockCategory.Content, typeof(MarkdownProps), "¶"),
        new("image", "Image", BlockCategory.Content, typeof(ImageProps), "▣"),
        new("button", "Button", BlockCategory.Content, typeof(ButtonProps), "⏺"),
        new("quote", "Quote", BlockCategory.Content, typeof(QuoteProps), "❝"),
        new("spacer", "Spacer", BlockCategory.Layout, typeof(SpacerProps), "↕"),
        new("divider", "Divider", BlockCategory.Layout, typeof(DividerProps), "—"),
        new("html", "HTML", BlockCategory.Content, typeof(HtmlProps), "</>", RequiredSystemGroup: SystemGroups.Html),
        new("postlist", "Latest posts", BlockCategory.Dynamic, typeof(PostListProps), "☰"),
        new("menu", "Menu", BlockCategory.Site, typeof(MenuProps), "≡"),
        new("siteheader", "Site header", BlockCategory.Site, typeof(SiteHeaderProps), "⌂",
            OnlyIn: [TreeKind.Header]),
    ];

    private static readonly Dictionary<string, BlockDefinition> ByType = All.ToDictionary(d => d.Type, StringComparer.Ordinal);

    public static BlockDefinition? Get(string type) => ByType.GetValueOrDefault(type);

    /// <summary>Blocks the user may insert into a tree of this kind.</summary>
    public static IEnumerable<BlockDefinition> Palette(TreeKind kind, EffectivePermissions perms) =>
        All.Where(d => d.AvailableIn(kind) && (d.RequiredSystemGroup is null || perms.HasSystem(d.RequiredSystemGroup, Perm.Rw)));
}

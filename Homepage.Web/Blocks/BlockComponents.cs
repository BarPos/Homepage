namespace Homepage.Web.Blocks;

/// <summary>Block type → the Razor component that renders it (the same on the site and in the editor canvas).</summary>
public static class BlockComponents
{
    private static readonly Dictionary<string, Type> Map = new(StringComparer.Ordinal)
    {
        ["section"] = typeof(SectionBlock),
        ["columns"] = typeof(ColumnsBlock),
        ["column"] = typeof(ColumnBlock),
        ["group"] = typeof(GroupBlock),
        ["heading"] = typeof(HeadingBlock),
        ["markdown"] = typeof(MarkdownBlock),
        ["image"] = typeof(ImageBlock),
        ["button"] = typeof(ButtonBlock),
        ["quote"] = typeof(QuoteBlock),
        ["spacer"] = typeof(SpacerBlock),
        ["divider"] = typeof(DividerBlock),
        ["html"] = typeof(HtmlBlock),
        ["postlist"] = typeof(PostListBlock),
        ["menu"] = typeof(MenuBlock),
        ["sitebar"] = typeof(SiteBarBlock),
        ["sitebrand"] = typeof(SiteBrandBlock),
    };

    public static Type? Get(string type) => Map.GetValueOrDefault(type);

    public static IEnumerable<string> Types => Map.Keys;
}

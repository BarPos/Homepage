using Homepage.Common;
using Homepage.Themes;

namespace Homepage.Web.Rendering;

/// <summary>Cascaded to every block component: site settings, theme, and whether this is the editor canvas.</summary>
public sealed class RenderContext
{
    public required SiteSettings Site { get; init; }
    public required ActiveTheme Theme { get; init; }
    /// <summary>True in the editor canvas: blocks get data-block-id wrappers and containers become drop zones.</summary>
    public bool Edit { get; init; }
    /// <summary>Which tree is editable in the canvas ("main", "header" or "footer"); the others render inert.</summary>
    public string EditRegion { get; init; } = "main";

    public RenderContext ForRegion(string region) => new()
    {
        Site = Site, Theme = Theme, EditRegion = EditRegion, Edit = Edit && EditRegion == region,
    };
}

public static class Css
{
    public static string Spacing(Homepage.Blocks.Spacing s) => s.ToString().ToLowerInvariant();
    public static string Align(Homepage.Blocks.Align a) => "al-" + a.ToString().ToLowerInvariant();

    public static string Ratio(Homepage.Blocks.ColumnRatio r) => r switch
    {
        Homepage.Blocks.ColumnRatio.OneTwo => "cols-1-2",
        Homepage.Blocks.ColumnRatio.TwoOne => "cols-2-1",
        Homepage.Blocks.ColumnRatio.OneThree => "cols-1-3",
        Homepage.Blocks.ColumnRatio.ThreeOne => "cols-3-1",
        _ => "",
    };

    /// <summary>Only known color tokens become classes; anything else falls back to the page background.</summary>
    public static string Background(string token) => ThemeTokens.ColorNames.Contains(token) ? "bg-" + token : "bg-bg";
}

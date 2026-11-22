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
    /// <summary>Which parts of the frame the page shows (its own setting; site pages are Full).</summary>
    public Homepage.Data.PageLayout Layout { get; init; }
    /// <summary>The page's own CSS (sanitized when rendered).</summary>
    public string PageCss { get; init; } = "";
    public bool NoIndex { get; init; }
    /// <summary>Addresses of pages, posts and blogs, for links stored as references (page:12, …).</summary>
    public Homepage.Content.SiteMap? Links { get; init; }

    /// <summary>
    /// The address for a link: references become the current address of their target. Null when the target is gone or
    /// not published (on the public site); the editor canvas shows drafts too.
    /// </summary>
    public string? Href(string url) => Links is null ? url : Links.Href(url, publishedOnly: !Edit);

    public RenderContext ForRegion(string region) => new()
    {
        Site = Site, Theme = Theme, EditRegion = EditRegion, Edit = Edit && EditRegion == region,
        Layout = Layout, PageCss = PageCss, NoIndex = NoIndex, Links = Links,
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

    /// <summary>A background class, or nothing for "no color".</summary>
    public static string? OptionalBackground(string token) => ThemeTokens.ColorNames.Contains(token) ? "bg-" + token + " has-bg" : null;

    public static string VAlign(Homepage.Blocks.VerticalAlign v) => "va-" + v.ToString().ToLowerInvariant();
    public static string Width(Homepage.Blocks.ContentWidth w) => "w-" + w.ToString().ToLowerInvariant();
    public static string Kebab(Enum e) => System.Text.RegularExpressions.Regex.Replace(e.ToString(), "(?<=[a-z])(?=[A-Z])", "-").ToLowerInvariant();
}

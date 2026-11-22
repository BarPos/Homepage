using System.ComponentModel.DataAnnotations;

namespace Homepage.Blocks;

// Props classes for the built-in blocks. The inspector form is generated from these (Display, Range, StringLength,
// DataType, and the Homepage attributes below); the server validates them on every update.

/// <summary>A choice of theme color token (rendered as a select of token names); with AllowNone, empty = no color.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ThemeColorAttribute : Attribute
{
    public bool AllowNone { get; init; }
}

/// <summary>A media item id (rendered as a media picker).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MediaPickerAttribute : Attribute;

/// <summary>A blog id; 0 = all blogs (rendered as a select of blogs).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BlogPickerAttribute : Attribute;

/// <summary>A menu key (rendered as a select of menus).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MenuPickerAttribute : Attribute;

/// <summary>
/// A link: http(s), mailto, tel, site-relative, or a reference to a page, post or blog of the site ("page:12",
/// "post:5", "blog:3"), which follows renames and moves. Validated server-side.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SafeUrlAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is not string s || s.Length == 0 || IsSafe(s);

    /// <summary>"page:12" → (page, 12). Kinds: page, post, blog.</summary>
    public static bool TryParseReference(string? url, out string kind, out int id)
    {
        kind = "";
        id = 0;
        if (url is null) return false;
        var m = System.Text.RegularExpressions.Regex.Match(url.Trim(), "^(page|post|blog):([0-9]{1,9})$");
        if (!m.Success) return false;
        kind = m.Groups[1].Value;
        id = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public static bool IsSafe(string url)
    {
        url = url.Trim();
        if (TryParseReference(url, out _, out _)) return true;
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)) return true;
        if (url.StartsWith('#')) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" or "mailto" or "tel";
    }
}

public enum Spacing { None, Sm, Md, Lg, Xl }
public enum Align { Left, Center, Right, Justify }
public enum SectionVariant { Default, Hero, Narrow, Band, Glass }
public enum ColumnRatio { Equal, OneTwo, TwoOne, OneThree, ThreeOne }
public enum ButtonStyle { Primary, Secondary, Outline }
public enum ImageWidth { Normal, Wide, Full }
public enum PostListLayout { List, Grid }
public enum BarStyle { Glass, Solid, Transparent }
public enum BarJustify { Spread, Start, Center, End }
public enum BrandDisplay { LogoAndText, LogoOnly, TextOnly }
public enum IconPosition { BeforeText, AfterText }
public enum Orientation { Horizontal, Vertical }
public enum MenuLook { Links, Pills, Underline, Buttons }
public enum VerticalAlign { Top, Center, Bottom, Stretch }
public enum ContentWidth { Narrow, Normal, Wide, Full }
public enum MinHeight { Auto, Third, Half, Full }
public enum ImageShape { Theme, Square, Rounded, Circle }
public enum ButtonSize { Small, Medium, Large }
public enum DividerLook { Line, Dashed, Dotted, Space }
public enum BarSize { Compact, Normal, Roomy }
public enum Justify { Start, Center, End, SpaceBetween }
public enum ItemsAlign { Start, Center, End, Stretch }

public sealed class SectionProps
{
    [Display(Name = "Style")] public SectionVariant Variant { get; set; } = SectionVariant.Default;
    [Display(Name = "Background"), ThemeColor] public string Background { get; set; } = "bg";
    [Display(Name = "Padding")] public Spacing Padding { get; set; } = Spacing.Lg;
    [Display(Name = "Content width")] public ContentWidth Width { get; set; } = ContentWidth.Normal;
    [Display(Name = "Text alignment")] public Align Align { get; set; } = Align.Left;
    [Display(Name = "Minimum height")] public MinHeight MinHeight { get; set; } = MinHeight.Auto;
    [Display(Name = "Content position (with a minimum height)")] public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Center;
    [Display(Name = "Background image"), MediaPicker] public int? BackgroundMediaId { get; set; }
    [Display(Name = "Darken the image (%)"), Range(0, 90)] public int Overlay { get; set; } = 40;
}

public sealed class ColumnsProps
{
    [Display(Name = "Column widths")] public ColumnRatio Ratio { get; set; } = ColumnRatio.Equal;
    [Display(Name = "Gap")] public Spacing Gap { get; set; } = Spacing.Md;
    [Display(Name = "Vertical alignment")] public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Stretch;
    [Display(Name = "Stack on small screens")] public bool StackOnMobile { get; set; } = true;
    [Display(Name = "Reverse the order when stacked")] public bool ReverseOnMobile { get; set; }
}

public sealed class ColumnProps
{
    [Display(Name = "Text alignment")] public Align Align { get; set; } = Align.Left;
    [Display(Name = "Vertical position")] public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Top;
    [Display(Name = "Background"), ThemeColor(AllowNone = true)] public string Background { get; set; } = "";
    [Display(Name = "Padding")] public Spacing Padding { get; set; } = Spacing.None;
}

/// <summary>A row or a vertical stack of blocks, with alignment and spacing.</summary>
public sealed class GroupProps
{
    [Display(Name = "Direction")] public Orientation Direction { get; set; } = Orientation.Horizontal;
    [Display(Name = "Distribute")] public Justify Justify { get; set; } = Justify.Start;
    [Display(Name = "Align items")] public ItemsAlign AlignItems { get; set; } = ItemsAlign.Center;
    [Display(Name = "Gap")] public Spacing Gap { get; set; } = Spacing.Sm;
    [Display(Name = "Wrap onto more lines")] public bool Wrap { get; set; } = true;
    [Display(Name = "Stack vertically on small screens")] public bool StackOnMobile { get; set; }
    [Display(Name = "Background"), ThemeColor(AllowNone = true)] public string Background { get; set; } = "";
    [Display(Name = "Padding")] public Spacing Padding { get; set; } = Spacing.None;
}

public sealed class HeadingProps
{
    [Display(Name = "Text"), Required, StringLength(300)] public string Text { get; set; } = "Heading";
    [Display(Name = "Level"), Range(1, 4)] public int Level { get; set; } = 2;
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
    [Display(Name = "Icon"), IconPicker, StringLength(80)] public string Icon { get; set; } = "";
    [Display(Name = "Link"), SafeUrl, StringLength(1000)] public string Url { get; set; } = "";
}

public sealed class MarkdownProps
{
    [Display(Name = "Markdown"), DataType(DataType.MultilineText), StringLength(200_000)]
    public string Markdown { get; set; } = "Write something **here**.";
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
}

public sealed class ImageProps
{
    [Display(Name = "Image"), MediaPicker] public int? MediaId { get; set; }
    [Display(Name = "Alternative text"), StringLength(500)] public string Alt { get; set; } = "";
    [Display(Name = "Caption"), StringLength(500)] public string Caption { get; set; } = "";
    [Display(Name = "Width")] public ImageWidth Width { get; set; } = ImageWidth.Normal;
    [Display(Name = "Maximum width (px, 0 = no limit)"), Range(0, 4000)] public int MaxWidth { get; set; }
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
    [Display(Name = "Shape")] public ImageShape Shape { get; set; } = ImageShape.Theme;
    [Display(Name = "Link"), SafeUrl, StringLength(1000)] public string Url { get; set; } = "";
    [Display(Name = "Open the link in a new tab")] public bool NewTab { get; set; }
}

public sealed class ButtonProps
{
    [Display(Name = "Text"), Required, StringLength(100)] public string Text { get; set; } = "Read more";
    [Display(Name = "Link"), SafeUrl, StringLength(1000)] public string Url { get; set; } = "/";
    [Display(Name = "Style")] public ButtonStyle Style { get; set; } = ButtonStyle.Primary;
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
    [Display(Name = "Open in a new tab")] public bool NewTab { get; set; }
    [Display(Name = "Icon"), IconPicker, StringLength(80)] public string Icon { get; set; } = "";
    [Display(Name = "Icon image (instead of an icon)"), MediaPicker] public int? IconMediaId { get; set; }
    [Display(Name = "Icon position")] public IconPosition IconPosition { get; set; } = IconPosition.BeforeText;
    [Display(Name = "Size")] public ButtonSize Size { get; set; } = ButtonSize.Medium;
    [Display(Name = "Full width")] public bool FullWidth { get; set; }
}

public sealed class QuoteProps
{
    [Display(Name = "Quote"), DataType(DataType.MultilineText), StringLength(2000)] public string Text { get; set; } = "A quote.";
    [Display(Name = "Attribution"), StringLength(200)] public string Cite { get; set; } = "";
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
}

public sealed class SpacerProps
{
    [Display(Name = "Size")] public Spacing Size { get; set; } = Spacing.Md;
    [Display(Name = "Fill the free space (in rows and site bars)")] public bool Grow { get; set; }
}

/// <summary>A horizontal line; in a row or site bar, a vertical one.</summary>
public sealed class DividerProps
{
    [Display(Name = "Style")] public DividerLook Look { get; set; } = DividerLook.Line;
    [Display(Name = "Space around")] public Spacing Spacing { get; set; } = Spacing.Md;
}

public sealed class HtmlProps
{
    [Display(Name = "HTML"), DataType(DataType.MultilineText), StringLength(100_000)] public string Html { get; set; } = "<p>HTML</p>";
}

public sealed class PostListProps
{
    [Display(Name = "Blog"), BlogPicker] public int BlogId { get; set; }
    [Display(Name = "Number of posts"), Range(1, 50)] public int Count { get; set; } = 5;
    [Display(Name = "Layout")] public PostListLayout Layout { get; set; } = PostListLayout.List;
    [Display(Name = "Show excerpts")] public bool ShowExcerpt { get; set; } = true;
    [Display(Name = "Show dates")] public bool ShowDate { get; set; } = true;
    [Display(Name = "Show authors")] public bool ShowAuthor { get; set; } = true;
    [Display(Name = "Columns (grid)"), Range(1, 4)] public int GridColumns { get; set; } = 2;
}

/// <summary>The bar at the top (or bottom) of the site: a row holding the brand, menus and buttons.</summary>
public sealed class SiteBarProps
{
    [Display(Name = "Stay on top while scrolling")] public bool Sticky { get; set; } = true;
    [Display(Name = "Style")] public BarStyle Style { get; set; } = BarStyle.Glass;
    [Display(Name = "Arrange items")] public BarJustify Justify { get; set; } = BarJustify.Spread;
    [Display(Name = "Width")] public ContentWidth Width { get; set; } = ContentWidth.Normal;
    [Display(Name = "Height")] public BarSize Size { get; set; } = BarSize.Normal;
    [Display(Name = "Gap")] public Spacing Gap { get; set; } = Spacing.Sm;
    [Display(Name = "Stack vertically on small screens")] public bool StackOnMobile { get; set; }
}

/// <summary>The site's logo and/or name, linking to the home page.</summary>
public sealed class SiteBrandProps
{
    [Display(Name = "Show")] public BrandDisplay Display { get; set; } = BrandDisplay.LogoAndText;
    [Display(Name = "Text (empty = site title)"), StringLength(100)] public string Text { get; set; } = "";
    [Display(Name = "Show tagline")] public bool ShowTagline { get; set; } = true;
    [Display(Name = "Logo height (px)"), Range(16, 120)] public int LogoHeight { get; set; } = 32;
}

public sealed class MenuProps
{
    [Display(Name = "Menu"), MenuPicker] public string MenuKey { get; set; } = "main";
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
    [Display(Name = "Direction")] public Orientation Orientation { get; set; } = Orientation.Horizontal;
    [Display(Name = "Look")] public MenuLook Look { get; set; } = MenuLook.Links;
    [Display(Name = "Gap")] public Spacing Gap { get; set; } = Spacing.Sm;
    [Display(Name = "Fold into a ☰ button on small screens")] public bool Collapse { get; set; }
}

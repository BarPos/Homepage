using System.ComponentModel.DataAnnotations;

namespace Homepage.Blocks;

// Props classes for the built-in blocks. The inspector form is generated from these (Display, Range, StringLength,
// DataType, and the Homepage attributes below); the server validates them on every update.

/// <summary>A choice of theme color token (rendered as a select of token names).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ThemeColorAttribute : Attribute;

/// <summary>A media item id (rendered as a media picker).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MediaPickerAttribute : Attribute;

/// <summary>A menu key (rendered as a select of menus).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MenuPickerAttribute : Attribute;

/// <summary>A link: http(s), mailto, tel, or site-relative. Validated server-side.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SafeUrlAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is not string s || s.Length == 0 || IsSafe(s);

    public static bool IsSafe(string url)
    {
        url = url.Trim();
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)) return true;
        if (url.StartsWith('#')) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" or "mailto" or "tel";
    }
}

public enum Spacing { None, Sm, Md, Lg, Xl }
public enum Align { Left, Center, Right }
public enum SectionVariant { Default, Hero, Narrow, Band, Glass }
public enum ColumnRatio { Equal, OneTwo, TwoOne, OneThree, ThreeOne }
public enum ButtonStyle { Primary, Secondary, Outline }
public enum ImageWidth { Normal, Wide, Full }
public enum PostListLayout { List, Grid }

public sealed class SectionProps
{
    [Display(Name = "Style")] public SectionVariant Variant { get; set; } = SectionVariant.Default;
    [Display(Name = "Background"), ThemeColor] public string Background { get; set; } = "bg";
    [Display(Name = "Padding")] public Spacing Padding { get; set; } = Spacing.Lg;
}

public sealed class ColumnsProps
{
    [Display(Name = "Column widths")] public ColumnRatio Ratio { get; set; } = ColumnRatio.Equal;
    [Display(Name = "Gap")] public Spacing Gap { get; set; } = Spacing.Md;
}

public sealed class ColumnProps;

public sealed class HeadingProps
{
    [Display(Name = "Text"), Required, StringLength(300)] public string Text { get; set; } = "Heading";
    [Display(Name = "Level"), Range(1, 4)] public int Level { get; set; } = 2;
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
}

public sealed class MarkdownProps
{
    [Display(Name = "Markdown"), DataType(DataType.MultilineText), StringLength(200_000)]
    public string Markdown { get; set; } = "Write something **here**.";
}

public sealed class ImageProps
{
    [Display(Name = "Image"), MediaPicker] public int? MediaId { get; set; }
    [Display(Name = "Alternative text"), StringLength(500)] public string Alt { get; set; } = "";
    [Display(Name = "Caption"), StringLength(500)] public string Caption { get; set; } = "";
    [Display(Name = "Width")] public ImageWidth Width { get; set; } = ImageWidth.Normal;
}

public sealed class ButtonProps
{
    [Display(Name = "Text"), Required, StringLength(100)] public string Text { get; set; } = "Read more";
    [Display(Name = "Link"), SafeUrl, StringLength(1000)] public string Url { get; set; } = "/";
    [Display(Name = "Style")] public ButtonStyle Style { get; set; } = ButtonStyle.Primary;
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
}

public sealed class QuoteProps
{
    [Display(Name = "Quote"), DataType(DataType.MultilineText), StringLength(2000)] public string Text { get; set; } = "A quote.";
    [Display(Name = "Attribution"), StringLength(200)] public string Cite { get; set; } = "";
}

public sealed class SpacerProps
{
    [Display(Name = "Size")] public Spacing Size { get; set; } = Spacing.Md;
}

public sealed class DividerProps;

public sealed class HtmlProps
{
    [Display(Name = "HTML"), DataType(DataType.MultilineText), StringLength(100_000)] public string Html { get; set; } = "<p>HTML</p>";
}

public sealed class PostListProps
{
    [Display(Name = "Number of posts"), Range(1, 50)] public int Count { get; set; } = 5;
    [Display(Name = "Layout")] public PostListLayout Layout { get; set; } = PostListLayout.List;
    [Display(Name = "Show excerpts")] public bool ShowExcerpt { get; set; } = true;
}

public sealed class SiteHeaderProps
{
    [Display(Name = "Menu"), MenuPicker] public string MenuKey { get; set; } = "main";
    [Display(Name = "Show tagline")] public bool ShowTagline { get; set; }
}

public sealed class MenuProps
{
    [Display(Name = "Menu"), MenuPicker] public string MenuKey { get; set; } = "main";
    [Display(Name = "Alignment")] public Align Align { get; set; } = Align.Left;
}

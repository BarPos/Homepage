using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Homepage.Themes;

/// <summary>A theme's design tokens (stored as jsonb). Every value is validated and clamped before it reaches CSS.</summary>
public sealed partial class ThemeTokens
{
    /// <summary>Color tokens a block may choose as a background (ThemeColor props).</summary>
    public static readonly IReadOnlySet<string> ColorNames = new HashSet<string>(StringComparer.Ordinal)
        { "bg", "surface", "surface-alt", "primary", "accent" };

    /// <summary>Every color token, with a label for the theme editor.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> AllColors =
    [
        ("bg", "Page background"), ("surface", "Surface"), ("surface-alt", "Alternate surface"),
        ("text", "Text"), ("muted", "Muted text"), ("border", "Borders"),
        ("primary", "Primary"), ("primary-text", "Text on primary"), ("accent", "Accent"), ("accent-text", "Text on accent"),
    ];

    public static readonly IReadOnlyDictionary<string, (string Label, string Stack)> Fonts = new Dictionary<string, (string, string)>
    {
        ["system-sans"] = ("System sans-serif", "system-ui, -apple-system, \"Segoe UI\", Roboto, \"Helvetica Neue\", Arial, sans-serif"),
        ["humanist"] = ("Humanist sans-serif", "Seravek, \"Gill Sans Nova\", Ubuntu, Calibri, \"DejaVu Sans\", source-sans-pro, sans-serif"),
        ["geometric"] = ("Geometric sans-serif", "Avenir, Montserrat, Corbel, \"URW Gothic\", source-sans-pro, sans-serif"),
        ["system-serif"] = ("Transitional serif", "Charter, \"Bitstream Charter\", \"Sitka Text\", Cambria, serif"),
        ["old-style"] = ("Old-style serif", "\"Iowan Old Style\", \"Palatino Linotype\", \"URW Palladio L\", P052, serif"),
        ["mono"] = ("Monospace", "ui-monospace, \"Cascadia Code\", \"Source Code Pro\", Menlo, Consolas, monospace"),
    };

    public Dictionary<string, string> Light { get; set; } = [];
    public Dictionary<string, string> Dark { get; set; } = [];
    /// <summary>auto (follows the visitor's system), light, or dark.</summary>
    public string ColorMode { get; set; } = "auto";
    public string BodyFont { get; set; } = "system-sans";
    public string HeadingFont { get; set; } = "system-sans";
    public double BaseSize { get; set; } = 17;
    public double Scale { get; set; } = 1.25;
    public double LineHeight { get; set; } = 1.6;
    public double Radius { get; set; } = 8;
    public string Shadow { get; set; } = "soft";
    public int ContainerWidth { get; set; } = 1100;
    public double SpacingUnit { get; set; } = 1;
    /// <summary>Frosted, translucent surfaces (header, cards, glass sections) over the page backdrop.</summary>
    public bool Glass { get; set; } = true;
    /// <summary>Blur behind glass surfaces, in px.</summary>
    public double GlassBlur { get; set; } = 18;
    /// <summary>How opaque glass surfaces are (0.3 = very clear, 0.95 = almost solid).</summary>
    public double GlassOpacity { get; set; } = 0.62;
    /// <summary>Page background behind everything: none (flat color), mesh (soft color fields) or aurora (bands of color).</summary>
    public string Backdrop { get; set; } = "mesh";

    public static readonly IReadOnlyList<(string Key, string Label)> Backdrops =
        [("none", "Flat color"), ("mesh", "Soft color fields"), ("aurora", "Aurora bands")];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ThemeTokens Parse(string? json)
    {
        ThemeTokens t;
        try { t = string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<ThemeTokens>(json, Json) ?? new(); }
        catch (JsonException) { t = new(); }
        return t.Normalized();
    }

    public string ToJson() => JsonSerializer.Serialize(Normalized(), Json);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    public static bool IsColor(string? s) => s is not null && HexColor().IsMatch(s);

    /// <summary>A copy with unknown keys dropped, bad colors replaced by defaults and numbers clamped.</summary>
    public ThemeTokens Normalized()
    {
        var d = Defaults;
        return new ThemeTokens
        {
            Light = AllColors.ToDictionary(c => c.Key, c => IsColor(Light.GetValueOrDefault(c.Key)) ? Light[c.Key].ToLowerInvariant() : d.Light[c.Key]),
            Dark = AllColors.ToDictionary(c => c.Key, c => IsColor(Dark.GetValueOrDefault(c.Key)) ? Dark[c.Key].ToLowerInvariant() : d.Dark[c.Key]),
            ColorMode = ColorMode is "auto" or "light" or "dark" ? ColorMode : "auto",
            BodyFont = Fonts.ContainsKey(BodyFont) ? BodyFont : "system-sans",
            HeadingFont = Fonts.ContainsKey(HeadingFont) ? HeadingFont : "system-sans",
            BaseSize = Clamp(BaseSize, 12, 24, 17),
            Scale = Clamp(Scale, 1.05, 1.6, 1.25),
            LineHeight = Clamp(LineHeight, 1.1, 2.2, 1.6),
            Radius = Clamp(Radius, 0, 32, 8),
            Shadow = Shadow is "none" or "soft" or "strong" ? Shadow : "soft",
            ContainerWidth = (int)Clamp(ContainerWidth, 600, 1600, 1100),
            SpacingUnit = Clamp(SpacingUnit, 0.5, 2, 1),
            Glass = Glass,
            GlassBlur = Clamp(GlassBlur, 0, 40, 18),
            GlassOpacity = Clamp(GlassOpacity, 0.3, 0.95, 0.62),
            Backdrop = Backdrops.Any(b => b.Key == Backdrop) ? Backdrop : "mesh",
        };
    }

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public const string ShadowSoft = "0 1px 2px rgb(16 18 27 / .05), 0 10px 30px -12px rgb(16 18 27 / .18)";
    public const string ShadowStrong = "0 2px 6px rgb(0 0 0 / .08), 0 28px 60px -20px rgb(0 0 0 / .38)";

    public static ThemeTokens Defaults { get; } = new()
    {
        Light = new()
        {
            ["bg"] = "#f3f2ee", ["surface"] = "#ffffff", ["surface-alt"] = "#e9e7e1", ["text"] = "#16171b",
            ["muted"] = "#5c606a", ["border"] = "#d9d6ce", ["primary"] = "#2a4fd6", ["primary-text"] = "#ffffff",
            ["accent"] = "#e0552c", ["accent-text"] = "#ffffff",
        },
        Dark = new()
        {
            ["bg"] = "#0d0f13", ["surface"] = "#171a20", ["surface-alt"] = "#1c2028", ["text"] = "#ecedf0",
            ["muted"] = "#9da2ad", ["border"] = "#2a2e37", ["primary"] = "#8ea6ff", ["primary-text"] = "#0a1030",
            ["accent"] = "#ff8d68", ["accent-text"] = "#200d06",
        },
    };

    /// <summary>The theme's CSS custom properties (and optional custom CSS), for /theme/{id}/{version}.css.</summary>
    public string ToCss(string customCss)
    {
        var t = Normalized();
        var sb = new StringBuilder();
        sb.Append(":root{");
        Var(sb, "font-body", Fonts[t.BodyFont].Stack);
        Var(sb, "font-heading", Fonts[t.HeadingFont].Stack);
        // Tight tracking flatters sans-serif display type; serifs need their spacing.
        var serif = t.HeadingFont is "system-serif" or "old-style";
        Var(sb, "tracking", serif ? "-.008em" : "-.03em");
        Var(sb, "tracking-hero", serif ? "-.015em" : "-.042em");
        Var(sb, "font-size", Num(t.BaseSize) + "px");
        Var(sb, "scale", Num(t.Scale));
        Var(sb, "line-height", Num(t.LineHeight));
        Var(sb, "radius", Num(t.Radius) + "px");
        Var(sb, "container", t.ContainerWidth + "px");
        Var(sb, "space", Num(t.SpacingUnit) + "rem");
        // Capsule buttons for rounded themes, matching corners for square ones.
        Var(sb, "btn-radius", t.Radius >= 10 ? "999px" : Num(t.Radius) + "px");
        Var(sb, "glass-blur", (t.Glass ? Num(t.GlassBlur) : "0") + "px");
        Var(sb, "glass-alpha", t.Glass ? Num(t.GlassOpacity) : "1");
        Var(sb, "shadow", t.Shadow switch
        {
            "none" => "none",
            "strong" => ShadowStrong,
            _ => ShadowSoft,
        });
        var lightFirst = t.ColorMode != "dark";
        Colors(sb, lightFirst ? t.Light : t.Dark);
        Var(sb, "color-scheme", lightFirst ? "light" : "dark");
        sb.Append('}');
        if (t.ColorMode == "auto")
        {
            sb.Append("@media (prefers-color-scheme: dark){:root{");
            Colors(sb, t.Dark);
            Var(sb, "color-scheme", "dark");
            sb.Append("}}");
        }
        sb.Append('\n').Append(SanitizeCustomCss(customCss));
        return sb.ToString();
    }

    private static void Colors(StringBuilder sb, Dictionary<string, string> colors)
    {
        foreach (var (key, _) in AllColors) Var(sb, "color-" + key, colors[key]);
        // Glass edges: a bright rim on light backgrounds, a faint one on dark backgrounds.
        var dark = Contrast(colors["bg"], "#000000") < Contrast(colors["bg"], "#ffffff");
        Var(sb, "glass-edge", dark ? "rgb(255 255 255 / .10)" : "rgb(255 255 255 / .70)");
        Var(sb, "glass-highlight", dark ? "rgb(255 255 255 / .07)" : "rgb(255 255 255 / .85)");
        Var(sb, "glass-sheen", dark ? ".06" : ".45");
    }

    private static void Var(StringBuilder sb, string name, string value) => sb.Append("--").Append(name).Append(':').Append(value).Append(';');

    private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"@import\b[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex ImportRule();

    [GeneratedRegex(@"/\*.*?(\*/|$)", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"(-webkit-)?image-set\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSet();

    // url( … ) whose argument is not a site path ("/x", not "//x") or a data: image.
    [GeneratedRegex(@"url\(\s*(?!['""]?\s*(?:/[^/]|data:image/))[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();

    /// <summary>
    /// Custom CSS (themes: rw only) may not load anything from other sites. CSS escapes (\75rl, \/) and comments are
    /// removed first, so they cannot hide a keyword or a path; then @import goes, image-set() is made invalid, and url()
    /// keeps only site paths and data: images. The steps repeat until nothing changes, so removed pieces cannot join
    /// into a new keyword. "&lt;" is removed so the CSS cannot close a style element.
    /// </summary>
    public static string SanitizeCustomCss(string? css)
    {
        if (string.IsNullOrEmpty(css)) return "";
        var s = css.Length > 100_000 ? css[..100_000] : css;
        for (var pass = 0; pass < 10; pass++)
        {
            var before = s;
            s = s.Replace("<", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal);
            s = Comment().Replace(s, "");
            s = ImportRule().Replace(s, "");
            s = ImageSet().Replace(s, "invalid(");
            s = ExternalUrl().Replace(s, "none");
            if (s == before) break;
        }
        return s;
    }

    /// <summary>WCAG 2 contrast ratio of two #rrggbb colors.</summary>
    public static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            double Ch(int i)
            {
                var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(0) + 0.7152 * Ch(1) + 0.0722 * Ch(2);
        }
        var (la, lb) = (L(a), L(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}

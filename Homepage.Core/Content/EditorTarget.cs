using System.Diagnostics.CodeAnalysis;
using Homepage.Blocks;

namespace Homepage.Content;

/// <summary>What the visual editor is editing: a page/post draft, or a theme's header or footer.</summary>
public readonly record struct EditorTarget(string Kind, int Id)
{
    public const string ContentKind = "content";
    public const string HeaderKind = "header";
    public const string FooterKind = "footer";

    public override string ToString() => $"{Kind}-{Id}";

    public static bool TryParse(string? s, [NotNullWhen(true)] out EditorTarget? target)
    {
        target = null;
        if (s is null) return false;
        var dash = s.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(s.AsSpan(dash + 1), out var id) || id <= 0) return false;
        var kind = s[..dash];
        if (kind is not (ContentKind or HeaderKind or FooterKind)) return false;
        target = new EditorTarget(kind, id);
        return true;
    }

    public bool IsTheme => Kind is HeaderKind or FooterKind;
}

public sealed record EditorDocument(EditorTarget Target, string Title, BlockTree Tree, TreeKind Kind, bool CanEdit, bool CanPublish,
    int? GroupId, int ThemeId);

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Homepage.Blocks;

namespace Homepage.Themes;

/// <summary>Themes shipped with the release (embedded JSON). Seeded into the database; read-only there, duplicate to customize.</summary>
public static class BuiltInThemes
{
    public sealed record Definition(string Name, string Slug, ThemeTokens Tokens);

    public static IReadOnlyList<Definition> All { get; } = Load();

    private static List<Definition> Load()
    {
        var asm = typeof(BuiltInThemes).Assembly;
        var list = new List<Definition>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Themes.BuiltIn.", StringComparison.Ordinal)).Order())
        {
            using var s = asm.GetManifestResourceStream(name)!;
            var doc = JsonNode.Parse(s)!.AsObject();
            list.Add(new Definition(doc["name"]!.GetValue<string>(), doc["slug"]!.GetValue<string>(),
                ThemeTokens.Parse(doc["tokens"]!.ToJsonString())));
        }
        // "clean" first: it is the default active theme.
        return list.OrderBy(d => d.Slug == "clean" ? 0 : 1).ThenBy(d => d.Name).ToList();
    }

    public static BlockTree DefaultHeader() => new()
    {
        Blocks = [new BlockNode { Type = "siteheader", Props = BlockOps.ToJson(new SiteHeaderProps { ShowTagline = true }) }],
    };

    public static BlockTree DefaultFooter() => new()
    {
        Blocks =
        [
            new BlockNode
            {
                Type = "section",
                Props = BlockOps.ToJson(new SectionProps { Background = "surface", Padding = Spacing.Md }),
                Children =
                [
                    new BlockNode { Type = "menu", Props = BlockOps.ToJson(new MenuProps { MenuKey = "footer", Align = Align.Center }) },
                    new BlockNode { Type = "markdown", Props = BlockOps.ToJson(new MarkdownProps { Markdown = "Made with Homepage." }) },
                ],
            },
        ],
    };
}

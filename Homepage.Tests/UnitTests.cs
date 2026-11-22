using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Homepage.Blocks;
using Homepage.Common;
using Homepage.Content;
using Homepage.Media;
using Homepage.Permissions;
using Homepage.Shared;
using Homepage.Themes;
using Homepage.Updates;

namespace Homepage.Tests;

public class VersionTests
{
    [Fact]
    public void DirectoryBuildPropsMatchesAppVersion()
    {
        var props = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Directory.Build.props.copy"));
        var version = Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal(AppVersion.Current, version);
        Assert.True(AppVersion.IsPlainTriplet(version));
    }
}

public class PermissionTests
{
    private static readonly Dictionary<string, int> Names = new()
    {
        [SystemGroups.Admin] = 1, [SystemGroups.Themes] = 2, [SystemGroups.Layout] = 3, [SystemGroups.Html] = 4, ["blog"] = 10, ["docs"] = 11,
    };

    private static EffectivePermissions P(int user, params (int Group, Perm Perm)[] grants) =>
        new(user, grants.Select(g => new Grant(g.Group, g.Perm)), Names);

    [Fact]
    public void GrantsAreAdditiveAndRwImpliesEverything()
    {
        var p = P(5, (10, Perm.Ro), (10, Perm.Publish), (11, Perm.Rw));
        Assert.True(p.Has(10, Perm.Ro));
        Assert.True(p.Has(10, Perm.Publish));
        Assert.False(p.Has(10, Perm.Edit));
        foreach (var (perm, _) in PermNames.All) Assert.True(p.Has(11, perm));
        Assert.False(p.Has(1, Perm.Ro));
        Assert.False(p.IsAdmin);
    }

    [Fact]
    public void AdminRwGivesEverythingEverywhere()
    {
        var p = P(1, (1, Perm.Rw));
        Assert.True(p.IsAdmin);
        Assert.True(p.Has(10, Perm.Delete));
        Assert.True(p.HasSystem(SystemGroups.Html, Perm.Rw));
        Assert.Equal(Names.Values.Order(), p.GroupsWith(Perm.Ro).Order());
    }

    [Fact]
    public void AdminRoIsNotAdmin()
    {
        var p = P(1, (1, Perm.Ro));
        Assert.False(p.IsAdmin);
        Assert.False(p.Has(10, Perm.Ro));
    }

    [Fact]
    public void NoGrantsMeansNoAdminArea() => Assert.False(P(9).CanSeeAdmin);

    [Theory]
    // perm on group 10, author?, ever published → can edit, can delete
    [InlineData(Perm.Create, true, false, true, true)]
    [InlineData(Perm.Create, true, true, false, false)]   // own but published: needs edit/delete
    [InlineData(Perm.Create, false, false, false, false)] // someone else's draft
    [InlineData(Perm.Edit, false, true, true, false)]
    [InlineData(Perm.Delete, false, true, false, true)]
    [InlineData(Perm.Rw, false, true, true, true)]
    [InlineData(Perm.Ro, true, false, false, false)]
    public void ContentRulesMatrix(Perm perm, bool own, bool published, bool canEdit, bool canDelete)
    {
        var p = P(5, (10, perm));
        var facts = new ContentFacts(10, own ? 5 : 7, published);
        Assert.Equal(canEdit, ContentRules.CanEdit(p, facts));
        Assert.Equal(canDelete, ContentRules.CanDelete(p, facts));
    }

    [Fact]
    public void MovingNeedsRwOnSourceAndCreateOnTarget()
    {
        var facts = new ContentFacts(10, 5, false);
        Assert.False(ContentRules.CanMove(P(5, (10, Perm.Edit), (11, Perm.Create)), facts, 11));
        Assert.False(ContentRules.CanMove(P(5, (10, Perm.Rw)), facts, 11));
        Assert.True(ContentRules.CanMove(P(5, (10, Perm.Rw), (11, Perm.Create)), facts, 11));
    }

    [Fact]
    public void PermissionKeysRoundTrip()
    {
        foreach (var (perm, key) in PermNames.All)
        {
            Assert.True(PermNames.TryParse(key, out var parsed));
            Assert.Equal(perm, parsed);
        }
        Assert.False(PermNames.TryParse("owner", out _));
    }
}

public class BlockTests
{
    private static readonly Dictionary<string, int> Names = new() { [SystemGroups.Admin] = 1, [SystemGroups.Html] = 4, ["c"] = 10 };
    private static EffectivePermissions Author => new(5, [new Grant(10, Perm.Create)], Names);
    private static EffectivePermissions HtmlUser => new(6, [new Grant(10, Perm.Rw), new Grant(4, Perm.Rw)], Names);

    [Fact]
    public void InsertMoveDeleteDuplicate()
    {
        var ops = new BlockOps(new BlockTree(), TreeKind.Page, Author);
        var section = ops.Insert("section", null, 0);
        var heading = ops.Insert("heading", section, 0);
        var text = ops.Insert("markdown", section, 1);
        Assert.Equal(["heading", "markdown"], ops.Tree.Find(section)!.Children.Select(c => c.Type));

        ops.Move(text, section, 0); // final position 0
        Assert.Equal([text, heading], ops.Tree.Find(section)!.Children.Select(c => c.Id));

        ops.Move(text, section, 1);
        Assert.Equal([heading, text], ops.Tree.Find(section)!.Children.Select(c => c.Id));

        ops.Move(text, null, 0); // out of the section to the top level
        Assert.Equal([text, section], ops.Tree.Blocks.Select(b => b.Id));

        var copy = ops.Duplicate(section);
        Assert.NotEqual(section, copy);
        Assert.Equal(3, ops.Tree.Blocks.Count);
        Assert.NotEqual(ops.Tree.Find(section)!.Children[0].Id, ops.Tree.Find(copy)!.Children[0].Id);

        ops.Delete(copy);
        Assert.Null(ops.Tree.Find(copy));
        Assert.Empty(BlockValidator.Validate(ops.Tree, TreeKind.Page));
    }

    [Fact]
    public void ColumnsComeWithTwoColumns()
    {
        var ops = new BlockOps(new BlockTree(), TreeKind.Page, Author);
        var cols = ops.Insert("columns", null, 0);
        Assert.Equal(["column", "column"], ops.Tree.Find(cols)!.Children.Select(c => c.Type));
    }

    [Fact]
    public void NestingRulesAreEnforced()
    {
        var ops = new BlockOps(new BlockTree(), TreeKind.Page, Author);
        var section = ops.Insert("section", null, 0);
        Assert.Throws<BlockException>(() => ops.Insert("section", section, 0));      // no section in section
        Assert.Throws<BlockException>(() => ops.Insert("column", null, 0));          // column only in columns
        var heading = ops.Insert("heading", section, 0);
        Assert.Throws<BlockException>(() => ops.Insert("markdown", heading, 0));     // not a container
        Assert.Throws<BlockException>(() => ops.Move(section, section, 0));          // into itself
        Assert.Throws<BlockException>(() => ops.Insert("sitebrand", null, 0));       // header/footer-only block
    }

    [Fact]
    public void HtmlBlockNeedsHtmlRw()
    {
        Assert.Throws<AccessDeniedException>(() => new BlockOps(new BlockTree(), TreeKind.Page, Author).Insert("html", null, 0));
        var ops = new BlockOps(new BlockTree(), TreeKind.Page, HtmlUser);
        var id = ops.Insert("html", null, 0);
        var asAuthor = new BlockOps(ops.Tree, TreeKind.Page, Author);
        Assert.Throws<AccessDeniedException>(() => asAuthor.Delete(id));
        Assert.Throws<AccessDeniedException>(() => asAuthor.Update(id, new JsonObject { ["html"] = "<b>x</b>" }));
        Assert.DoesNotContain(BlockRegistry.Palette(TreeKind.Page, Author), d => d.Type == "html");
        Assert.Contains(BlockRegistry.Palette(TreeKind.Page, HtmlUser), d => d.Type == "html");
    }

    [Fact]
    public void UpdateValidatesProps()
    {
        var ops = new BlockOps(new BlockTree(), TreeKind.Page, Author);
        var b = ops.Insert("button", null, 0);
        Assert.Throws<BlockException>(() => ops.Update(b, new JsonObject { ["text"] = "Go", ["url"] = "javascript:alert(1)" }));
        ops.Update(b, new JsonObject { ["text"] = "Go", ["url"] = "https://example.com" });
        var s = ops.Insert("section", null, 0);
        Assert.Throws<BlockException>(() => ops.Update(s, new JsonObject { ["background"] = "url(evil)" }));
    }

    [Fact]
    public void UnknownBlocksSurviveRoundTripAndValidation()
    {
        const string json = """{"schemaVersion":1,"blocks":[{"id":"b1","type":"future-thing","props":{"x":1},"children":[]}]}""";
        var tree = BlockTree.Parse(json);
        Assert.Empty(BlockValidator.Validate(tree, TreeKind.Page));
        Assert.Contains("future-thing", tree.ToJson());
        Assert.Throws<BlockException>(() => new BlockOps(tree, TreeKind.Page, Author).Insert("future-thing", null, 0));
    }

    [Fact]
    public void ValidatorRejectsDuplicateIdsAndBadNesting()
    {
        const string json = """{"blocks":[{"id":"a","type":"heading","props":{"text":"x"}},{"id":"a","type":"column"}]}""";
        var errors = BlockValidator.Validate(BlockTree.Parse(json), TreeKind.Page);
        Assert.Contains(errors, e => e.Contains("Duplicate"));
        Assert.Contains(errors, e => e.Contains("Column"));
    }

    [Fact]
    public void BinderReadsFormValues()
    {
        var def = BlockRegistry.Get("heading")!;
        var form = new Dictionary<string, string> { ["p.Text"] = "Hi", ["p.Level"] = "3", ["p.Align"] = "Center" };
        var props = BlockPropsBinder.Bind(def, k => form.GetValueOrDefault(k));
        var model = new BlockNode { Type = "heading", Props = props }.PropsAs<HeadingProps>();
        Assert.Equal(("Hi", 3, Align.Center), (model.Text, model.Level, model.Align));
        form["p.Align"] = "Sideways";
        Assert.Throws<BlockException>(() => BlockPropsBinder.Bind(def, k => form.GetValueOrDefault(k)));
    }
}

public class ContentMigrationTests
{
    private sealed class RenameText : IContentMigration
    {
        public int FromVersion => 1;
        public void Migrate(JsonObject document)
        {
            foreach (var b in document["blocks"]!.AsArray())
                if (b!["type"]!.GetValue<string>() == "heading") b["props"]!["text"] = b["props"]!["title"]!.GetValue<string>();
        }
    }

    [Fact]
    public void MigratesStepByStepAndIsIdempotent()
    {
        var migrator = new ContentMigrator([new RenameText()], targetVersion: 2);
        var migrated = migrator.MigrateJson("""{"schemaVersion":1,"blocks":[{"id":"h","type":"heading","props":{"title":"Old"}}]}""")!;
        Assert.Contains("\"schemaVersion\":2", migrated);
        Assert.Equal("Old", BlockTree.Parse(migrated).Find("h")!.PropsAs<HeadingProps>().Text);
        Assert.Null(migrator.MigrateJson(migrated));
    }

    [Fact]
    public void RefusesNewerSchemasAndGaps()
    {
        Assert.Throws<InvalidDataException>(() => ContentMigrator.Default.MigrateJson("""{"schemaVersion":99,"blocks":[]}"""));
        Assert.Throws<InvalidDataException>(() => new ContentMigrator([], 3).MigrateJson("""{"schemaVersion":1,"blocks":[]}"""));
        Assert.Null(ContentMigrator.Default.MigrateJson($$"""{"schemaVersion":{{BlockTree.CurrentSchemaVersion}},"blocks":[]}"""));
    }

    /// <summary>Golden schema-1 header (as stored by release 0.1.0): migrates and validates.</summary>
    [Fact]
    public void V1HeaderMigratesToAnEditableSiteBar()
    {
        const string v1 = """{"schemaVersion":1,"blocks":[{"id":"bf00000001","type":"siteheader","props":{"menuKey":"main","showTagline":true},"children":[]}]}""";
        var migrated = ContentMigrator.Default.MigrateJson(v1)!;
        var tree = BlockTree.Parse(migrated);
        Assert.Equal(2, tree.SchemaVersion);
        var bar = tree.Blocks.Single();
        Assert.Equal(("bf00000001", "sitebar"), (bar.Id, bar.Type));
        Assert.Equal("main", bar.Children[1].PropsAs<MenuProps>().MenuKey);
        Assert.Empty(BlockValidator.Validate(tree, TreeKind.Header));
        Assert.Null(ContentMigrator.Default.MigrateJson(migrated));
    }
}

public class ThemeTests
{
    [Fact]
    public void TokensAreNormalized()
    {
        var t = new ThemeTokens
        {
            Light = new() { ["bg"] = "red; } body { x", ["primary"] = "#ABCDEF" },
            BaseSize = 500, BodyFont = "Comic Sans", ColorMode = "neon", ContainerWidth = 5,
        }.Normalized();
        Assert.Equal(ThemeTokens.Defaults.Light["bg"], t.Light["bg"]);
        Assert.Equal("#abcdef", t.Light["primary"]);
        Assert.Equal(24, t.BaseSize);
        Assert.Equal("system-sans", t.BodyFont);
        Assert.Equal("auto", t.ColorMode);
        Assert.Equal(600, t.ContainerWidth);
    }

    [Fact]
    public void CssHasAutoDarkModeAndSanitizedCustomCss()
    {
        var css = ThemeTokens.Defaults.ToCss("@import url(https://evil.example/x.css); .a{background:url(https://evil.example/t.png)} .b{background:url(/media/x.png)} </style><script>");
        Assert.Contains("--color-bg:#f3f2ee", css);
        Assert.Contains("--glass-blur:18px", css);
        Assert.Contains("--btn-radius:", css);
        Assert.Contains("@media (prefers-color-scheme: dark)", css);
        Assert.DoesNotContain("@import", css);
        Assert.DoesNotContain("evil.example", css);
        Assert.Contains("url(/media/x.png)", css);
        Assert.DoesNotContain("</style", css);
    }

    [Fact]
    public void BuiltInThemesLoad()
    {
        Assert.Contains(BuiltInThemes.All, t => t.Slug == "clean");
        Assert.Equal("clean", BuiltInThemes.All[0].Slug);
        Assert.True(BuiltInThemes.All.Count >= 3);
    }

    [Fact]
    public void ContrastRatio()
    {
        Assert.Equal(21, ThemeTokens.Contrast("#000000", "#ffffff"), 1);
        Assert.Equal(1, ThemeTokens.Contrast("#777777", "#777777"), 3);
    }
}

public class UpdateFileTests
{
    [Fact]
    public void RequestFileHasTheKeysUpdateShParses()
    {
        var id = Guid.NewGuid();
        var text = UpdateRequestFile.BuildRequest(id, "1.2.0", "1.1.0", "x64", ReleaseAssetNames.ServerTarball("1.2.0", "x64"), new string('a', 64));
        var kv = UpdateRequestFile.ParseKeyValues(text);
        Assert.Equal("1", kv["format"]);
        Assert.Equal(id.ToString("D"), kv["run_id"]);
        Assert.Equal("Homepage.Server-1.2.0-linux-x64.tar.gz", kv["file"]);
        Assert.Equal(["arch", "file", "format", "from_version", "run_id", "sha256", "version"], kv.Keys.Order());
    }

    [Fact]
    public void UpdateShUsesTheSameTarballName()
    {
        var root = FindRepoRoot();
        var updateSh = File.ReadAllText(Path.Combine(root, "deploy", "linux", "update.sh"));
        Assert.Contains("Homepage.Server-%s-linux-%s.tar.gz", updateSh);
    }

    [Fact]
    public void Sha256SumsParse()
    {
        var sums = Sha256Sums.Parse($"{new string('A', 64)}  Homepage.Server-1.0.0-linux-x64.tar.gz\n{new string('b', 64)} *install.sh\njunk\n");
        Assert.Equal(new string('a', 64), sums["Homepage.Server-1.0.0-linux-x64.tar.gz"]);
        Assert.True(sums.ContainsKey("install.sh"));
        Assert.Equal(2, sums.Count);
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Homepage.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public class MiscTests
{
    [Theory]
    [InlineData("Hello World!", "hello-world")]
    [InlineData("  Zażółć gęślą jaźń ", "zazolc-gesla-jazn")]
    [InlineData("---", "")]
    public void Slugs(string input, string expected) => Assert.Equal(expected, Slug.From(input));

    [Fact]
    public void MediaSniffingUsesContentNotExtension()
    {
        Assert.Equal("image/png", MediaService.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0])?.Type);
        Assert.Equal("application/pdf", MediaService.Sniff("%PDF-1.7"u8)?.Type);
        Assert.Null(MediaService.Sniff("<html><script>"u8));
        Assert.Null(MediaService.Sniff("<svg xmlns="u8));
    }

    [Fact]
    public void MarkdownIsSanitized()
    {
        var md = new MarkdownRenderer();
        var html = md.ToHtml("**bold** <script>alert(1)</script> [x](javascript:alert(1)) <img src=x onerror=alert(1)>");
        Assert.Contains("<strong>bold</strong>", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("onerror", html);
    }

    [Theory]
    [InlineData("/about", true)]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("//evil.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    public void SafeUrls(string url, bool ok) => Assert.Equal(ok, SafeUrlAttribute.IsSafe(url));
}

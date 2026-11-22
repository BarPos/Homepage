using System.Text.Json.Nodes;

namespace Homepage.Blocks;

/// <summary>One step of a stored-content migration, from <see cref="FromVersion"/> to FromVersion + 1.</summary>
public interface IContentMigration
{
    int FromVersion { get; }
    /// <summary>Rewrites a whole tree document (<c>{ schemaVersion, blocks }</c>) in place.</summary>
    void Migrate(JsonObject document);
}

/// <summary>
/// Brings stored block trees up to <see cref="BlockTree.CurrentSchemaVersion"/>. Runs in <c>--migrate-only</c>
/// after the EF migrations, which update.sh calls before switching releases; a failure therefore rolls back
/// through the normal pg_dump restore. Register a migration here whenever a block's props change shape.
/// </summary>
public sealed class ContentMigrator(IEnumerable<IContentMigration> migrations, int targetVersion = BlockTree.CurrentSchemaVersion)
{
    private readonly Dictionary<int, IContentMigration> _steps = migrations.ToDictionary(m => m.FromVersion);

    public static ContentMigrator Default { get; } = new([new V1SiteHeaderToSiteBar()]);

    /// <summary>Returns the migrated JSON, or null when it is already current.</summary>
    public string? MigrateJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var doc = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("A block tree is not a JSON object.");
        var version = doc["schemaVersion"]?.GetValue<int>() ?? 1;
        if (version > targetVersion)
            throw new InvalidDataException($"Content schema {version} is newer than this release supports ({targetVersion}).");
        if (version == targetVersion) return null;
        while (version < targetVersion)
        {
            if (!_steps.TryGetValue(version, out var step))
                throw new InvalidDataException($"No content migration from schema {version}.");
            step.Migrate(doc);
            version++;
            doc["schemaVersion"] = version;
        }
        return doc.ToJsonString();
    }
}

/// <summary>
/// Schema 1 → 2: the fixed "siteheader" block becomes an editable "sitebar" holding a "sitebrand" and a "menu",
/// wherever it is in the tree (top level or inside a section).
/// </summary>
public sealed class V1SiteHeaderToSiteBar : IContentMigration
{
    public int FromVersion => 1;

    public void Migrate(JsonObject document)
    {
        if (document["blocks"] is JsonArray blocks) Walk(blocks);
    }

    private static void Walk(JsonArray list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject node) continue;
            if (node["type"]?.GetValue<string>() == "siteheader")
            {
                var props = node["props"] as JsonObject;
                var menuKey = props?["menuKey"]?.GetValue<string>() ?? "main";
                var showTagline = props?["showTagline"]?.GetValue<bool>() ?? false;
                list[i] = new JsonObject
                {
                    ["id"] = node["id"]?.GetValue<string>() ?? BlockTree.NewId(),
                    ["type"] = "sitebar",
                    ["props"] = new JsonObject { ["sticky"] = true, ["style"] = (int)BarStyle.Glass, ["justify"] = (int)BarJustify.Spread },
                    ["children"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = BlockTree.NewId(), ["type"] = "sitebrand",
                            ["props"] = new JsonObject { ["display"] = (int)BrandDisplay.LogoAndText, ["text"] = "", ["showTagline"] = showTagline, ["logoHeight"] = 32 },
                            ["children"] = new JsonArray(),
                        },
                        new JsonObject
                        {
                            ["id"] = BlockTree.NewId(), ["type"] = "menu",
                            ["props"] = new JsonObject { ["menuKey"] = menuKey, ["align"] = (int)Align.Right },
                            ["children"] = new JsonArray(),
                        }),
                };
                continue;
            }
            if (node["children"] is JsonArray children) Walk(children);
        }
    }
}

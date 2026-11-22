using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Homepage.Blocks;

/// <summary>
/// A page's content: a tree of typed blocks, stored as JSON (jsonb).
/// <code>{ "schemaVersion": 1, "blocks": [ { "id": "b1", "type": "section", "props": { … }, "children": [ … ] } ] }</code>
/// Props stay a <see cref="JsonObject"/> here, so unknown block types (from a newer or older release) survive a
/// load/save round trip; each block definition reads them into its typed props class when rendering or validating.
/// </summary>
public sealed class BlockTree
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<BlockNode> Blocks { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static BlockTree Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BlockTree();
        return JsonSerializer.Deserialize<BlockTree>(json, Json) ?? new BlockTree();
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public BlockTree Clone() => Parse(ToJson());

    /// <summary>Every node, depth first.</summary>
    public IEnumerable<BlockNode> All() => Walk(Blocks);

    private static IEnumerable<BlockNode> Walk(List<BlockNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Walk(n.Children)) yield return c;
        }
    }

    public BlockNode? Find(string id) => All().FirstOrDefault(n => n.Id == id);

    /// <summary>The list that contains <paramref name="id"/> and its parent (null parent = top level).</summary>
    public (List<BlockNode> List, BlockNode? Parent)? FindContainer(string id)
    {
        if (Blocks.Any(b => b.Id == id)) return (Blocks, null);
        foreach (var n in All())
            if (n.Children.Any(c => c.Id == id)) return (n.Children, n);
        return null;
    }

    public static string NewId() => "b" + Guid.NewGuid().ToString("N")[..10];
}

public sealed class BlockNode
{
    public string Id { get; set; } = BlockTree.NewId();
    public string Type { get; set; } = "";
    public JsonObject Props { get; set; } = [];
    public List<BlockNode> Children { get; set; } = [];

    public T PropsAs<T>() where T : new()
    {
        try { return Props.Deserialize<T>(BlockTree.Json) ?? new T(); }
        catch (JsonException) { return new T(); }
    }
}

using System.Text.Json.Nodes;
using Homepage.Permissions;

namespace Homepage.Blocks;

/// <summary>
/// Editor operations on a draft tree. Each op validates nesting, the tree kind and block-type permissions
/// (for example the HTML block needs html: rw) before changing anything. Pure: no I/O.
/// </summary>
public sealed class BlockOps(BlockTree tree, TreeKind kind, EffectivePermissions perms)
{
    public BlockTree Tree { get; } = tree;

    public string Insert(string type, string? parentId, int index)
    {
        var def = BlockRegistry.Get(type) ?? throw new BlockException($"Unknown block type '{type}'.");
        if (!def.AvailableIn(kind)) throw new BlockException($"'{def.Label}' is not available here.");
        RequireTypePermission(def);
        var node = new BlockNode { Type = type, Props = ToJson(def.CreateDefaultProps()) };
        if (type == "columns")
            node.Children = [new BlockNode { Type = "column" }, new BlockNode { Type = "column" }];
        var list = TargetList(parentId, def);
        list.Insert(Math.Clamp(index, 0, list.Count), node);
        return node.Id;
    }

    public void Move(string id, string? parentId, int index)
    {
        var node = Tree.Find(id) ?? throw new BlockException("The block no longer exists.");
        var def = BlockRegistry.Get(node.Type) ?? throw new BlockException("Unknown blocks cannot be moved.");
        RequireTypePermission(def);
        if (parentId is not null && (parentId == id || Descendants(node).Any(d => d.Id == parentId)))
            throw new BlockException("A block cannot be moved into itself.");
        var target = TargetList(parentId, def);
        // index is the final position in the target list (what SortableJS reports as newIndex).
        var (source, _) = Tree.FindContainer(id)!.Value;
        source.Remove(node);
        target.Insert(Math.Clamp(index, 0, target.Count), node);
    }

    public void Update(string id, JsonObject props)
    {
        var node = Tree.Find(id) ?? throw new BlockException("The block no longer exists.");
        var def = BlockRegistry.Get(node.Type) ?? throw new BlockException("Unknown blocks cannot be edited.");
        RequireTypePermission(def);
        var errors = BlockValidator.ValidateProps(def, props);
        if (errors.Count > 0) throw new BlockException(string.Join(" ", errors));
        node.Props = props;
    }

    public void Delete(string id)
    {
        var node = Tree.Find(id) ?? throw new BlockException("The block no longer exists.");
        if (BlockRegistry.Get(node.Type) is { } def) RequireTypePermission(def);
        foreach (var d in Descendants(node))
            if (BlockRegistry.Get(d.Type) is { } dd) RequireTypePermission(dd);
        var (list, _) = Tree.FindContainer(id)!.Value;
        list.Remove(node);
    }

    public string Duplicate(string id)
    {
        var node = Tree.Find(id) ?? throw new BlockException("The block no longer exists.");
        foreach (var n in Descendants(node).Prepend(node))
            if (BlockRegistry.Get(n.Type) is { } d) RequireTypePermission(d);
            else throw new BlockException("Unknown blocks cannot be duplicated.");
        var copy = CloneWithNewIds(node);
        var (list, _) = Tree.FindContainer(id)!.Value;
        list.Insert(list.IndexOf(node) + 1, copy);
        return copy.Id;
    }

    private List<BlockNode> TargetList(string? parentId, BlockDefinition child)
    {
        if (parentId is null)
        {
            if (!child.AllowedUnder(null)) throw new BlockException($"'{child.Label}' must be placed inside another block.");
            return Tree.Blocks;
        }
        var parent = Tree.Find(parentId) ?? throw new BlockException("The target block no longer exists.");
        var pdef = BlockRegistry.Get(parent.Type) ?? throw new BlockException("Unknown blocks cannot contain blocks.");
        if (!pdef.AllowsChild(child.Type) || !child.AllowedUnder(parent.Type))
            throw new BlockException($"'{pdef.Label}' cannot contain '{child.Label}'.");
        if (Depth(parent) + 1 > BlockValidator.MaxDepth) throw new BlockException("Blocks are nested too deeply.");
        return parent.Children;
    }

    private int Depth(BlockNode node)
    {
        var depth = 1;
        var current = node;
        while (Tree.FindContainer(current.Id) is { Parent: { } p }) { depth++; current = p; }
        return depth;
    }

    private void RequireTypePermission(BlockDefinition def)
    {
        if (def.RequiredSystemGroup is { } g && !perms.HasSystem(g, Perm.Rw))
            throw new Content.AccessDeniedException($"You may not use the '{def.Label}' block.");
    }

    private static IEnumerable<BlockNode> Descendants(BlockNode n) =>
        n.Children.SelectMany(c => Descendants(c).Prepend(c));

    private static BlockNode CloneWithNewIds(BlockNode n) => new()
    {
        Type = n.Type,
        Props = (JsonObject)n.Props.DeepClone(),
        Children = n.Children.Select(CloneWithNewIds).ToList(),
    };

    internal static JsonObject ToJson(object props) =>
        (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(props, props.GetType(), BlockTree.Json)!;
}

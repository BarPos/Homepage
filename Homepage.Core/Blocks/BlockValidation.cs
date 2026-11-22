using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Homepage.Permissions;
using Homepage.Themes;

namespace Homepage.Blocks;

public sealed class BlockException(string message) : Exception(message);

/// <summary>Structural and props validation of a whole tree. Unknown block types are kept (not inserted, not rendered).</summary>
public static partial class BlockValidator
{
    public const int MaxNodes = 1000;
    public const int MaxDepth = 6;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex IdRegex();

    public static IReadOnlyList<string> Validate(BlockTree tree, TreeKind kind)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        void Walk(List<BlockNode> nodes, BlockNode? parent, int depth)
        {
            if (depth > MaxDepth) { errors.Add($"Blocks are nested deeper than {MaxDepth} levels."); return; }
            foreach (var n in nodes)
            {
                if (++count > MaxNodes) { errors.Add($"More than {MaxNodes} blocks."); return; }
                if (!IdRegex().IsMatch(n.Id)) errors.Add($"Bad block id '{n.Id}'.");
                else if (!ids.Add(n.Id)) errors.Add($"Duplicate block id '{n.Id}'.");
                var def = BlockRegistry.Get(n.Type);
                if (def is null) continue; // unknown: preserved, never rendered
                if (!def.AllowedUnder(parent?.Type)) errors.Add($"'{def.Label}' cannot be placed there.");
                if (parent is not null && BlockRegistry.Get(parent.Type) is { } pd && !pd.AllowsChild(n.Type))
                    errors.Add($"'{pd.Label}' cannot contain '{def.Label}'.");
                if (!def.AvailableIn(kind)) errors.Add($"'{def.Label}' is not available here.");
                if (!def.IsContainer && n.Children.Count > 0) errors.Add($"'{def.Label}' cannot contain blocks.");
                errors.AddRange(ValidateProps(def, n.Props));
                Walk(n.Children, n, depth + 1);
            }
        }
        Walk(tree.Blocks, null, 1);
        return errors;
    }

    public static IReadOnlyList<string> ValidateProps(BlockDefinition def, JsonObject props)
    {
        object model;
        try { model = props.Deserialize(def.PropsType, BlockTree.Json) ?? def.CreateDefaultProps(); }
        catch (JsonException) { return [$"'{def.Label}': invalid settings."]; }
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        var errors = results.Select(r => $"'{def.Label}': {r.ErrorMessage}").ToList();
        foreach (var p in def.PropsType.GetProperties().Where(p => p.GetCustomAttribute<ThemeColorAttribute>() is not null))
            if (p.GetValue(model) is string s && !ThemeTokens.ColorNames.Contains(s))
                errors.Add($"'{def.Label}': unknown color '{s}'.");
        return errors;
    }
}

/// <summary>Turns inspector form values (<c>p.PropertyName</c>) into a props object for a block type.</summary>
public static class BlockPropsBinder
{
    public static JsonObject Bind(BlockDefinition def, Func<string, string?> form)
    {
        var obj = new JsonObject();
        foreach (var p in def.PropsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            var raw = form("p." + p.Name);
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (t == typeof(bool))
            {
                obj[name] = raw is "true" or "on";
            }
            else if (t == typeof(int))
            {
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) obj[name] = i;
                else if (Nullable.GetUnderlyingType(p.PropertyType) is not null) obj[name] = null;
                else throw new BlockException($"'{p.Name}' must be a number.");
            }
            else if (t.IsEnum)
            {
                if (raw is null || !Enum.TryParse(t, raw, ignoreCase: false, out var e) || !Enum.IsDefined(t, e!))
                    throw new BlockException($"'{p.Name}' has an invalid value.");
                obj[name] = JsonValue.Create((int)e!);
            }
            else if (t == typeof(string))
            {
                obj[name] = (raw ?? "").Replace("\r\n", "\n");
            }
        }
        return obj;
    }
}

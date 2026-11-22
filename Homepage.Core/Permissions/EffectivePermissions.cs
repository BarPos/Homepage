namespace Homepage.Permissions;

/// <summary>One grant, from a direct user ↔ group row or from a role ↔ group row of one of the user's roles.</summary>
public readonly record struct Grant(int GroupId, Perm Perm);

/// <summary>
/// A user's effective permissions: the union of direct and role grants on valid groups (additive only, no deny).
/// <c>admin: rw</c> grants every permission on every group.
/// </summary>
public sealed class EffectivePermissions
{
    private readonly Dictionary<int, Perm> _byGroup;
    private readonly Dictionary<string, int> _groupIdsByName;

    public EffectivePermissions(int userId, IEnumerable<Grant> grants, IReadOnlyDictionary<string, int> groupIdsByName)
    {
        UserId = userId;
        _groupIdsByName = new Dictionary<string, int>(groupIdsByName, StringComparer.Ordinal);
        _byGroup = [];
        foreach (var g in grants)
            _byGroup[g.GroupId] = _byGroup.GetValueOrDefault(g.GroupId) | g.Perm;
        foreach (var key in _byGroup.Keys.ToList())
            _byGroup[key] = PermNames.Expand(_byGroup[key]);
        IsAdmin = _groupIdsByName.TryGetValue(SystemGroups.Admin, out var adminId)
                  && _byGroup.GetValueOrDefault(adminId).HasFlag(Perm.Rw);
    }

    public static EffectivePermissions Nobody { get; } = new(0, [], new Dictionary<string, int>());

    public int UserId { get; }
    public bool IsAdmin { get; }

    /// <summary>The flags on a group (everything for admins on a known group).</summary>
    public Perm On(int groupId) => IsAdmin && _groupIdsByName.ContainsValue(groupId) ? PermNames.Expand(Perm.Rw) : _byGroup.GetValueOrDefault(groupId);

    public bool Has(int groupId, Perm perm) => (On(groupId) & perm) == perm;

    public bool HasSystem(string groupName, Perm perm) =>
        IsAdmin || (_groupIdsByName.TryGetValue(groupName, out var id) && Has(id, perm));

    /// <summary>Valid groups on which the user has <paramref name="perm"/> (for SQL filters).</summary>
    public IReadOnlyList<int> GroupsWith(Perm perm) =>
        IsAdmin ? _groupIdsByName.Values.ToList() : _byGroup.Where(kv => (kv.Value & perm) == perm).Select(kv => kv.Key).ToList();

    public bool CanSeeAdmin => IsAdmin || _byGroup.Values.Any(v => v != Perm.None);
}

namespace Homepage.Permissions;

/// <summary>The minimal facts about a content item that the rules need.</summary>
public readonly record struct ContentFacts(int GroupId, int? AuthorId, bool EverPublished);

/// <summary>What a user may do with content items (pages, posts, media, menus, templates) in a group.</summary>
public static class ContentRules
{
    public static bool CanView(EffectivePermissions p, ContentFacts c) => p.Has(c.GroupId, Perm.Ro);

    public static bool CanCreateIn(EffectivePermissions p, int groupId) => p.Has(groupId, Perm.Create);

    /// <summary><c>edit</c>, or <c>create</c> on one's own item that was never published.</summary>
    public static bool CanEdit(EffectivePermissions p, ContentFacts c) =>
        p.Has(c.GroupId, Perm.Edit) || IsOwnUnpublished(p, c);

    public static bool CanPublish(EffectivePermissions p, ContentFacts c) => p.Has(c.GroupId, Perm.Publish);

    public static bool CanDelete(EffectivePermissions p, ContentFacts c) =>
        p.Has(c.GroupId, Perm.Delete) || IsOwnUnpublished(p, c);

    /// <summary>Moving needs <c>rw</c> on the source group and <c>create</c> on the target (OTRS move_into).</summary>
    public static bool CanMove(EffectivePermissions p, ContentFacts c, int targetGroupId) =>
        c.GroupId == targetGroupId || (p.Has(c.GroupId, Perm.Rw) && p.Has(targetGroupId, Perm.Create));

    private static bool IsOwnUnpublished(EffectivePermissions p, ContentFacts c) =>
        p.Has(c.GroupId, Perm.Create) && c.AuthorId == p.UserId && !c.EverPublished;
}

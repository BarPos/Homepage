namespace Homepage.Permissions;

/// <summary>
/// Permission types on a group, modelled on OTRS Community Edition (<c>ro</c> … <c>rw</c>).
/// Stored as one row per granted type; <see cref="Rw"/> implies every other type.
/// </summary>
[Flags]
public enum Perm
{
    None = 0,
    /// <summary>See items in the admin, including drafts and revisions.</summary>
    Ro = 1,
    /// <summary>Create items, and edit and delete one's own unpublished items.</summary>
    Create = 2,
    /// <summary>Edit anyone's items (drafts).</summary>
    Edit = 4,
    /// <summary>Publish, unpublish, schedule, restore revisions.</summary>
    Publish = 8,
    /// <summary>Delete and restore anyone's items.</summary>
    Delete = 16,
    /// <summary>Everything, plus moving items out of the group.</summary>
    Rw = 32,
}

public static class PermNames
{
    /// <summary>The types in display order, with their storage keys.</summary>
    public static readonly IReadOnlyList<(Perm Perm, string Key)> All =
    [
        (Perm.Ro, "ro"), (Perm.Create, "create"), (Perm.Edit, "edit"),
        (Perm.Publish, "publish"), (Perm.Delete, "delete"), (Perm.Rw, "rw"),
    ];

    public static string ToKey(Perm p) => All.First(a => a.Perm == p).Key;

    public static bool TryParse(string? key, out Perm perm)
    {
        foreach (var (p, k) in All)
            if (string.Equals(k, key, StringComparison.Ordinal)) { perm = p; return true; }
        perm = Perm.None;
        return false;
    }

    /// <summary>The effective flags of a set of grants: rw expands to every type.</summary>
    public static Perm Expand(Perm granted) =>
        granted.HasFlag(Perm.Rw) ? Perm.Ro | Perm.Create | Perm.Edit | Perm.Publish | Perm.Delete | Perm.Rw : granted;
}

/// <summary>Names of the built-in system groups that gate admin modules (OTRS: module Group/GroupRo registration).</summary>
public static class SystemGroups
{
    public const string Admin = "admin";
    public const string Themes = "themes";
    public const string Layout = "layout";
    public const string Html = "html";
    public const string Forms = "forms";
    public const string Audit = "audit";
    public const string Analytics = "analytics";
    public const string Mail = "mail";
    /// <summary>Default content group (not a system group; it can be renamed or made invalid).</summary>
    public const string Content = "content";

    public static readonly IReadOnlyList<(string Name, string Comment)> All =
    [
        (Admin, "Users, groups, roles, permissions, settings, updates. Full access everywhere."),
        (Themes, "ro: view themes. rw: edit, activate themes and custom CSS."),
        (Layout, "rw: header, footer and menus."),
        (Html, "rw: use the raw HTML block."),
        (Forms, "ro: view forms and their submissions. rw: build forms, export and delete submissions."),
        (Audit, "ro: view the audit log."),
        (Analytics, "ro: view visitor statistics. rw: change analytics settings and delete the data."),
        (Mail, "ro: view mailing lists, subscribers and sent mail. rw: manage lists and email templates, send email to lists."),
    ];
}

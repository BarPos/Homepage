using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Permissions;

/// <summary>
/// Administration of groups, roles and the three OTRS-style relations (users ↔ groups, roles ↔ groups, users ↔ roles).
/// Every method needs admin: rw; every change bumps the permission version and is audited.
/// </summary>
public sealed class PermissionAdminService(IDbContextFactory<AppDbContext> dbFactory, IPermissionService permissions, IAuditLog audit,
    TimeProvider time)
{
    private static void RequireAdmin(EffectivePermissions p)
    {
        if (!p.IsAdmin) throw new AccessDeniedException("This needs admin: rw.");
    }

    public async Task<Group> SaveGroupAsync(EffectivePermissions p, int? id, string name, string comment, bool valid, string actor)
    {
        RequireAdmin(p);
        name = name.Trim().ToLowerInvariant();
        if (name.Length is < 2 or > 100 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new Blocks.BlockException("Group names are 2 to 100 characters: letters, digits, - and _.");
        await using var db = await dbFactory.CreateDbContextAsync();
        Group g;
        if (id is { } gid)
        {
            g = await db.Groups.FirstOrDefaultAsync(x => x.Id == gid) ?? throw new NotFoundException();
            if (g.IsSystem && (g.Name != name || !valid)) throw new Blocks.BlockException("System groups cannot be renamed or made invalid.");
        }
        else
        {
            g = new Group { CreatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.Groups.Add(g);
        }
        if (await db.Groups.AnyAsync(x => x.Name == name && x.Id != g.Id)) throw new ConflictException($"A group '{name}' already exists.");
        g.Name = name;
        g.Comment = comment.Trim();
        g.Valid = valid;
        await db.SaveChangesAsync();
        await permissions.BumpVersionAsync(db);
        await audit.WriteAsync(actor, id is null ? "group.create" : "group.update", "Group", g.Id.ToString(), $"{name} valid={valid}");
        return g;
    }

    public async Task<Role> SaveRoleAsync(EffectivePermissions p, int? id, string name, string comment, bool valid, string actor)
    {
        RequireAdmin(p);
        name = name.Trim();
        if (name.Length is < 2 or > 100) throw new Blocks.BlockException("Role names are 2 to 100 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        Role r;
        if (id is { } rid) r = await db.PermissionRoles.FirstOrDefaultAsync(x => x.Id == rid) ?? throw new NotFoundException();
        else
        {
            r = new Role { CreatedAtUtc = time.GetUtcNow().UtcDateTime };
            db.PermissionRoles.Add(r);
        }
        if (await db.PermissionRoles.AnyAsync(x => x.Name == name && x.Id != r.Id)) throw new ConflictException($"A role '{name}' already exists.");
        r.Name = name;
        r.Comment = comment.Trim();
        r.Valid = valid;
        await db.SaveChangesAsync();
        await permissions.BumpVersionAsync(db);
        await audit.WriteAsync(actor, id is null ? "role.create" : "role.update", "Role", r.Id.ToString(), $"{name} valid={valid}");
        return r;
    }

    /// <summary>Grants or revokes one permission type of a user on a group (OTRS: Agents ↔ Groups).</summary>
    public async Task SetUserGroupAsync(EffectivePermissions p, int userId, int groupId, Perm perm, bool granted, string actor)
    {
        RequireAdmin(p);
        var key = PermNames.ToKey(perm);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Users.AnyAsync(u => u.Id == userId) || !await db.Groups.AnyAsync(g => g.Id == groupId)) throw new NotFoundException();
        var row = await db.UserGroups.FirstOrDefaultAsync(x => x.UserId == userId && x.GroupId == groupId && x.Permission == key);
        if (granted && row is null) db.UserGroups.Add(new UserGroup { UserId = userId, GroupId = groupId, Permission = key });
        else if (!granted && row is not null)
        {
            await GuardLastAdminAsync(db, p, userId, groupId, perm);
            db.UserGroups.Remove(row);
        }
        else return;
        await db.SaveChangesAsync();
        await permissions.BumpVersionAsync(db);
        await audit.WriteAsync(actor, granted ? "grant.user" : "revoke.user", "UserGroup", $"{userId}:{groupId}", key);
    }

    /// <summary>Roles ↔ Groups.</summary>
    public async Task SetRoleGroupAsync(EffectivePermissions p, int roleId, int groupId, Perm perm, bool granted, string actor)
    {
        RequireAdmin(p);
        var key = PermNames.ToKey(perm);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.PermissionRoles.AnyAsync(r => r.Id == roleId) || !await db.Groups.AnyAsync(g => g.Id == groupId)) throw new NotFoundException();
        var row = await db.RoleGroups.FirstOrDefaultAsync(x => x.RoleId == roleId && x.GroupId == groupId && x.Permission == key);
        if (granted && row is null) db.RoleGroups.Add(new RoleGroup { RoleId = roleId, GroupId = groupId, Permission = key });
        else if (!granted && row is not null) db.RoleGroups.Remove(row);
        else return;
        await db.SaveChangesAsync();
        await permissions.BumpVersionAsync(db);
        await audit.WriteAsync(actor, granted ? "grant.role" : "revoke.role", "RoleGroup", $"{roleId}:{groupId}", key);
    }

    /// <summary>Users ↔ Roles.</summary>
    public async Task SetUserRoleAsync(EffectivePermissions p, int userId, int roleId, bool member, string actor)
    {
        RequireAdmin(p);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Users.AnyAsync(u => u.Id == userId) || !await db.PermissionRoles.AnyAsync(r => r.Id == roleId)) throw new NotFoundException();
        var row = await db.UserRoles.FirstOrDefaultAsync(x => x.UserId == userId && x.RoleId == roleId);
        if (member && row is null) db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        else if (!member && row is not null) db.UserRoles.Remove(row);
        else return;
        await db.SaveChangesAsync();
        await permissions.BumpVersionAsync(db);
        await audit.WriteAsync(actor, member ? "role.add" : "role.remove", "UserRole", $"{userId}:{roleId}", "");
    }

    /// <summary>Refuses to remove the signed-in admin's own direct admin: rw (the CLI is the way back, but avoid the foot-gun).</summary>
    private static async Task GuardLastAdminAsync(AppDbContext db, EffectivePermissions p, int userId, int groupId, Perm perm)
    {
        if (perm != Perm.Rw || userId != p.UserId) return;
        var admin = await db.Groups.FirstAsync(g => g.Name == SystemGroups.Admin);
        if (admin.Id == groupId) throw new Blocks.BlockException("You cannot remove your own admin: rw. Ask another administrator.");
    }
}

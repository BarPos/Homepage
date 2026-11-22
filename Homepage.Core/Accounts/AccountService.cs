using Homepage.Common;
using Homepage.Data;
using Homepage.Permissions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Accounts;

public sealed record OperationResult(bool Ok, string? Error = null)
{
    public static OperationResult Success { get; } = new(true);
    public static OperationResult Fail(string error) => new(false, error);
}

/// <summary>Accounts: creation (CLI and admin), password resets, validity. Identity handles hashing and lockout.</summary>
public sealed class AccountService(
    UserManager<User> users,
    IDbContextFactory<AppDbContext> dbFactory,
    IPermissionService permissions,
    IAuditLog audit,
    TimeProvider time)
{
    public const int MinPasswordLength = 10;

    public async Task<(OperationResult Result, User? User)> CreateAsync(string userName, string displayName, string password,
        string actor, bool makeAdmin = false)
    {
        userName = userName.Trim();
        if (userName.Length is < 2 or > 64 || !userName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@'))
            return (OperationResult.Fail("User names are 2 to 64 characters: letters, digits, . _ - @"), null);
        if (password.Length < MinPasswordLength)
            return (OperationResult.Fail($"Passwords need at least {MinPasswordLength} characters."), null);
        var user = new User
        {
            UserName = userName, DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) return (OperationResult.Fail(string.Join(" ", created.Errors.Select(e => e.Description))), null);
        if (makeAdmin)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var adminGroup = await db.Groups.FirstAsync(g => g.Name == SystemGroups.Admin);
            db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = adminGroup.Id, Permission = PermNames.ToKey(Perm.Rw) });
            await db.SaveChangesAsync();
            await permissions.BumpVersionAsync(db);
        }
        await audit.WriteAsync(actor, "user.create", "User", user.Id.ToString(), userName + (makeAdmin ? " (admin)" : ""));
        return (OperationResult.Success, user);
    }

    public async Task<OperationResult> ResetPasswordAsync(int userId, string password, string actor)
    {
        if (password.Length < MinPasswordLength) return OperationResult.Fail($"Passwords need at least {MinPasswordLength} characters.");
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return OperationResult.Fail("No such user.");
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var r = await users.ResetPasswordAsync(user, token, password);
        if (!r.Succeeded) return OperationResult.Fail(string.Join(" ", r.Errors.Select(e => e.Description)));
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await audit.WriteAsync(actor, "user.password", "User", user.Id.ToString(), user.UserName ?? "");
        return OperationResult.Success;
    }

    public async Task SetValidAsync(int userId, bool valid, string actor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId) ?? throw new Content.NotFoundException();
        user.Valid = valid;
        if (!valid) user.SecurityStamp = Guid.NewGuid().ToString("N"); // ends every session, also after re-enabling
        await db.SaveChangesAsync();
        await permissions.BumpVersionAsync(db);
        await audit.WriteAsync(actor, valid ? "user.valid" : "user.invalid", "User", userId.ToString(), user.UserName ?? "");
    }
}

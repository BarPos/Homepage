using System.Security.Claims;
using Homepage.Permissions;
using Microsoft.AspNetCore.Antiforgery;

namespace Homepage.Web.Rendering;

/// <summary>The signed-in admin user for one request (set by <see cref="AdminFilter"/>).</summary>
public sealed class AdminContext
{
    public const string ItemKey = "homepage.admin";
    public required int UserId { get; init; }
    public required string UserName { get; init; }
    public required string DisplayName { get; init; }
    public required EffectivePermissions Perms { get; init; }
    public required string CsrfToken { get; init; }
    public string? Ip { get; init; }

    public string Actor => UserName;

    public static AdminContext? From(HttpContext ctx) => ctx.Items.TryGetValue(ItemKey, out var v) ? v as AdminContext : null;
    public static AdminContext Get(HttpContext ctx) => From(ctx) ?? throw new InvalidOperationException("No admin context.");
}

/// <summary>
/// Runs for every signed-in admin request:
/// <list type="bullet">
/// <item>the session must still be current: the cookie's security stamp must match the account (a password reset or
/// making the account invalid changes it and ends every session), and it must be younger than <see cref="AdminAuth.MaxSessionAge"/>;</item>
/// <item>every request that changes something (not GET/HEAD) must carry the antiforgery token, whether or not the
/// endpoint reads a form;</item>
/// <item>permissions are resolved from the database (not stored in the cookie), and users who can see nothing are refused.</item>
/// </list>
/// </summary>
public sealed class AdminFilter(IPermissionService permissions, IAntiforgery antiforgery,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Homepage.Data.AppDbContext> dbFactory, TimeProvider time) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var idText = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idText, out var userId)) return Results.Redirect("/admin/login");

        if (!await SessionIsCurrentAsync(http, userId))
        {
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(http, AdminAuth.Scheme);
            return Results.Redirect("/admin/login");
        }

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            // UseAntiforgery() has validated the token (the admin group requires it) but only enforces it when an
            // endpoint reads a form. Enforce it here for every admin endpoint.
            var feature = http.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>();
            var valid = feature?.IsValid ?? await antiforgery.IsRequestValidAsync(http);
            if (!valid) return Results.Problem("The form has expired or did not come from this site. Reload the page and try again.", statusCode: 400);
        }

        var perms = await permissions.GetAsync(userId, http.RequestAborted);
        if (!perms.CanSeeAdmin)
        {
            await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(http, AdminAuth.Scheme);
            return Results.Redirect("/admin/login?denied=1");
        }
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Items[AdminContext.ItemKey] = new AdminContext
        {
            UserId = userId,
            UserName = http.User.Identity?.Name ?? "",
            DisplayName = http.User.FindFirstValue("display_name") ?? http.User.Identity?.Name ?? "",
            Perms = perms,
            CsrfToken = tokens.RequestToken ?? "",
            Ip = http.Connection.RemoteIpAddress?.ToString(),
        };
        return await next(context);
    }

    private async Task<bool> SessionIsCurrentAsync(HttpContext http, int userId)
    {
        if (!long.TryParse(http.User.FindFirstValue(AdminAuth.IssuedClaim), out var issued)) return false;
        var age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issued);
        if (age > AdminAuth.MaxSessionAge || age < TimeSpan.FromMinutes(-5)) return false;
        var stamp = http.User.FindFirstValue(AdminAuth.StampClaim);
        if (string.IsNullOrEmpty(stamp)) return false;
        await using var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
        var current = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Users.Where(u => u.Id == userId).Select(u => new { u.SecurityStamp, u.Valid }), http.RequestAborted);
        return current is { Valid: true } && string.Equals(current.SecurityStamp, stamp, StringComparison.Ordinal);
    }
}

public static class AdminAuth
{
    public const string Scheme = "Homepage.Admin";
    /// <summary>The account's security stamp at sign-in; a password reset or invalidation changes it.</summary>
    public const string StampClaim = "hp_stamp";
    /// <summary>Sign-in time (Unix seconds); sessions end after <see cref="MaxSessionAge"/> even while in use.</summary>
    public const string IssuedClaim = "hp_issued";
    public static readonly TimeSpan MaxSessionAge = TimeSpan.FromHours(12);
}

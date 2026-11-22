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
/// Loads the user's effective permissions for every admin endpoint and refuses users who can see nothing
/// (invalid users, or users without any grant). Permissions are resolved on every request, not stored in the cookie,
/// so revoking access takes effect immediately.
/// </summary>
public sealed class AdminFilter(IPermissionService permissions, IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var idText = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idText, out var userId)) return Results.Redirect("/admin/login");
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
}

public static class AdminAuth
{
    public const string Scheme = "Homepage.Admin";
}

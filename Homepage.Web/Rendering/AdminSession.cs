using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Homepage.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace Homepage.Web.Rendering;

/// <summary>Issuing the admin cookie, and the short-lived "password checked, now the code" step of two-factor sign-in.</summary>
public static class AdminSession
{
    public static Task SignInAsync(HttpContext http, User user, string? signInIp, DateTimeOffset issued)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, user.UserName ?? ""),
            new Claim("display_name", user.DisplayName),
            new Claim(AdminAuth.StampClaim, user.SecurityStamp ?? ""),
            new Claim(AdminAuth.IssuedClaim, issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new Claim(AdminAuth.SignInIpClaim, signInIp ?? ""),
        ], AdminAuth.Scheme);
        return http.SignInAsync(AdminAuth.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
    }

    /// <summary>
    /// After the signed-in user changed their own password, two-factor setting or display name: the security stamp may
    /// have changed, which would end this session too. Re-issue the cookie, keeping the sign-in time and address.
    /// </summary>
    public static Task RefreshAsync(HttpContext http, User user)
    {
        var issued = long.TryParse(http.User.FindFirstValue(AdminAuth.IssuedClaim), out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.UtcNow;
        return SignInAsync(http, user, http.User.FindFirstValue(AdminAuth.SignInIpClaim), issued);
    }

    // ------------------------------------------------------------ pending two-factor step

    private const string PendingCookie = "hp_2fa";
    private const string PendingPurpose = "Homepage.TwoFactorPending.v1";
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    public sealed record Pending(int UserId, string Stamp, string? ReturnUrl);

    public static void SetPending(HttpContext http, User user, string? returnUrl)
    {
        var protector = Protector(http);
        var value = protector.Protect(JsonSerializer.Serialize(new Pending(user.Id, user.SecurityStamp ?? "", returnUrl)), PendingLifetime);
        http.Response.Cookies.Append(PendingCookie, value, new CookieOptions
        {
            Path = "/admin", HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, MaxAge = PendingLifetime,
        });
    }

    public static Pending? GetPending(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(PendingCookie, out var value) || string.IsNullOrEmpty(value)) return null;
        try { return JsonSerializer.Deserialize<Pending>(Protector(http).Unprotect(value)); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException) { return null; }
    }

    public static void ClearPending(HttpContext http) => http.Response.Cookies.Delete(PendingCookie, new CookieOptions { Path = "/admin" });

    private static ITimeLimitedDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(PendingPurpose).ToTimeLimitedDataProtector();

    public static string SafeReturnUrl(string? r) =>
        r is { Length: > 1 } && r.StartsWith("/admin", StringComparison.Ordinal) && !r.StartsWith("//", StringComparison.Ordinal) && !r.Contains('\\') ? r : "/admin";
}

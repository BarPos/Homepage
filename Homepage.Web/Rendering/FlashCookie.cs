using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Homepage.Web.Rendering;

/// <summary>
/// One-time status messages after a redirect ("Saved.", or why something was refused). They travel in a short-lived
/// cookie protected with Data Protection, never in the URL, so nobody can make the admin show text of their choosing
/// with a crafted link. Read once, then deleted.
/// </summary>
public static class FlashCookie
{
    private const string Name = "hp_flash";
    private const string Purpose = "Homepage.FlashCookie.v1";

    private sealed record Payload(string Text, bool Error);

    public static IResult Redirect(HttpContext http, string url, string text, bool error = false)
    {
        var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose)
            .ToTimeLimitedDataProtector();
        var value = protector.Protect(JsonSerializer.Serialize(new Payload(text.Length > 1000 ? text[..1000] : text, error)), TimeSpan.FromMinutes(2));
        http.Response.Cookies.Append(Name, value, new CookieOptions
        {
            Path = "/admin", HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, MaxAge = TimeSpan.FromMinutes(2),
        });
        return Results.Redirect(url);
    }

    /// <summary>The pending message, if any (and removes it).</summary>
    public static (string? Message, string? Error) Take(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(Name, out var value) || string.IsNullOrEmpty(value)) return (null, null);
        http.Response.Cookies.Delete(Name, new CookieOptions { Path = "/admin" });
        try
        {
            var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose)
                .ToTimeLimitedDataProtector();
            var p = JsonSerializer.Deserialize<Payload>(protector.Unprotect(value));
            return p is null ? (null, null) : p.Error ? (null, p.Text) : (p.Text, null);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return (null, null); // forged, expired or from another key ring: show nothing
        }
    }
}

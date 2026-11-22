using Microsoft.AspNetCore.DataProtection;

namespace Homepage.Web.Rendering;

/// <summary>
/// Spam protection for public forms without cookies or captchas: each form carries a signed "rendered at" token
/// (submits faster than 2 seconds after rendering are refused, tokens older than a week too), and a hidden field that
/// people never see but bots fill in.
/// </summary>
public sealed class FormGuard(IDataProtectionProvider protection, TimeProvider time)
{
    public const string TokenField = "_hp_t";
    /// <summary>The trap: hidden from people with CSS; anything in it means a bot.</summary>
    public const string TrapField = "website";
    private readonly IDataProtector _p = protection.CreateProtector("Homepage.FormGuard.v1");

    public string Issue(string scope) => _p.Protect($"{scope}|{time.GetUtcNow().UtcTicks}");

    public enum Verdict { Ok, Trap, TooFast, Invalid }

    public Verdict Check(string scope, string? token, string? trap)
    {
        if (!string.IsNullOrEmpty(trap)) return Verdict.Trap;
        if (string.IsNullOrEmpty(token)) return Verdict.Invalid;
        string plain;
        try { plain = _p.Unprotect(token); } catch (System.Security.Cryptography.CryptographicException) { return Verdict.Invalid; }
        var parts = plain.Split('|');
        if (parts.Length != 2 || parts[0] != scope || !long.TryParse(parts[1], out var ticks)) return Verdict.Invalid;
        var age = time.GetUtcNow().UtcDateTime - new DateTime(ticks, DateTimeKind.Utc);
        if (age < TimeSpan.FromSeconds(2)) return Verdict.TooFast;
        if (age > TimeSpan.FromDays(7)) return Verdict.Invalid;
        return Verdict.Ok;
    }
}

using Homepage.Common;

namespace Homepage.Web.Rendering;

/// <summary>The current request's client address and the signed-in user's sign-in address, for audit entries.</summary>
public sealed class AuditRequestInfo(IHttpContextAccessor accessor) : IAuditRequestInfo
{
    public string? Ip => Normalize(accessor.HttpContext?.Connection.RemoteIpAddress);

    public string? SignInIp => accessor.HttpContext?.User.FindFirst(AdminAuth.SignInIpClaim)?.Value;

    /// <summary>IPv4 clients reached over a dual-stack socket show as plain IPv4.</summary>
    public static string? Normalize(System.Net.IPAddress? ip) =>
        ip is null ? null : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}

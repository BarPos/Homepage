using Homepage.Data;

namespace Homepage.Common;

public interface IAuditLog
{
    /// <summary>
    /// Writes an entry. <paramref name="ip"/> overrides the request's address; without it the entry takes the address
    /// of the current request and the address the user signed in from (see <see cref="IAuditRequestInfo"/>).
    /// </summary>
    Task WriteAsync(string actor, string action, string entity, string entityId, string details, string? ip = null, CancellationToken ct = default);
}

/// <summary>Where the current request comes from. Empty outside a request (background jobs, the command line).</summary>
public interface IAuditRequestInfo
{
    /// <summary>The client address of the current request.</summary>
    string? Ip { get; }
    /// <summary>The address the signed-in user signed in from (kept in their session).</summary>
    string? SignInIp { get; }
}

internal sealed class NoAuditRequestInfo : IAuditRequestInfo
{
    public string? Ip => null;
    public string? SignInIp => null;
}

internal sealed class AuditLog(Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext> dbFactory, TimeProvider time,
    IAuditRequestInfo request) : IAuditLog
{
    public async Task WriteAsync(string actor, string action, string entity, string entityId, string details, string? ip = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = time.GetUtcNow().UtcDateTime,
            Actor = Trim(actor, 200), Action = Trim(action, 100), Entity = Trim(entity, 100),
            EntityId = Trim(entityId, 100), Details = Trim(details, 4000),
            Ip = Clip(ip ?? request.Ip),
            SignInIp = Clip(request.SignInIp),
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? Clip(string? s) => s is null ? null : Trim(s, 64);
}

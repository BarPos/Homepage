using Homepage.Data;

namespace Homepage.Common;

public interface IAuditLog
{
    Task WriteAsync(string actor, string action, string entity, string entityId, string details, string? ip = null, CancellationToken ct = default);
}

internal sealed class AuditLog(Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext> dbFactory, TimeProvider time) : IAuditLog
{
    public async Task WriteAsync(string actor, string action, string entity, string entityId, string details, string? ip = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditLog.Add(new AuditEntry
        {
            AtUtc = time.GetUtcNow().UtcDateTime,
            Actor = Trim(actor, 200), Action = Trim(action, 100), Entity = Trim(entity, 100),
            EntityId = Trim(entityId, 100), Details = Trim(details, 4000), Ip = ip,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}

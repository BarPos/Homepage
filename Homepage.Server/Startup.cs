using Homepage.Blocks;
using Homepage.Data;
using Microsoft.EntityFrameworkCore;

namespace Homepage.Server;

public static class Startup
{
    /// <summary>EF migrations, then content migrations of every stored block tree, then first-run seed data.</summary>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
        await MigrateContentAsync(db, ContentMigrator.Default, ct);
        await Seeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>(), ct);
    }

    public static async Task MigrateContentAsync(AppDbContext db, ContentMigrator migrator, CancellationToken ct = default)
    {
        foreach (var c in await db.ContentItems.ToListAsync(ct))
        {
            if (migrator.MigrateJson(c.DraftBlocks) is { } d) c.DraftBlocks = d;
            if (migrator.MigrateJson(c.Blocks) is { } p) c.Blocks = p;
        }
        foreach (var r in await db.Revisions.ToListAsync(ct))
            if (migrator.MigrateJson(r.Blocks) is { } m) { r.Blocks = m; r.SchemaVersion = BlockTree.CurrentSchemaVersion; }
        foreach (var t in await db.Themes.ToListAsync(ct))
        {
            if (migrator.MigrateJson(t.HeaderBlocks) is { } h) t.HeaderBlocks = h;
            if (migrator.MigrateJson(t.FooterBlocks) is { } f) t.FooterBlocks = f;
        }
        await db.SaveChangesAsync(ct);
    }
}

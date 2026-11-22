using Homepage.Common;
using Homepage.Content;
using Homepage.Data;
using Homepage.Options;
using Homepage.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Homepage.Media;

/// <summary>
/// Uploads into Paths:Data/media/ under random names. The type is checked by content (magic bytes), not by the
/// extension; only images and PDFs are accepted, so nothing uploaded can be served as HTML or script.
/// </summary>
public sealed class MediaService(IDbContextFactory<AppDbContext> dbFactory, IOptions<PathsOptions> paths, IAuditLog audit, TimeProvider time)
{
    public const long MaxBytes = 20L * 1024 * 1024;

    private static readonly (string Type, string Ext, byte[] Magic, int Offset)[] Signatures =
    [
        ("image/png", ".png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0),
        ("image/jpeg", ".jpg", [0xFF, 0xD8, 0xFF], 0),
        ("image/gif", ".gif", "GIF8"u8.ToArray(), 0),
        ("image/webp", ".webp", "WEBP"u8.ToArray(), 8),
        ("image/avif", ".avif", "ftypavif"u8.ToArray(), 4),
        ("image/x-icon", ".ico", [0x00, 0x00, 0x01, 0x00], 0),
        ("application/pdf", ".pdf", "%PDF-"u8.ToArray(), 0),
    ];

    public static (string Type, string Ext)? Sniff(ReadOnlySpan<byte> head)
    {
        foreach (var s in Signatures)
            if (head.Length >= s.Offset + s.Magic.Length && head.Slice(s.Offset, s.Magic.Length).SequenceEqual(s.Magic))
                return (s.Type, s.Ext);
        return null;
    }

    public async Task<List<MediaItem>> ListAsync(EffectivePermissions p, CancellationToken ct = default)
    {
        var visible = p.GroupsWith(Perm.Ro);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Media.AsNoTracking().Where(m => visible.Contains(m.GroupId)).OrderByDescending(m => m.CreatedAtUtc).Take(500).ToListAsync(ct);
    }

    /// <summary>Paged, filtered list of the files the user can see.</summary>
    public async Task<PagedResult<MediaItem>> QueryAsync(EffectivePermissions p, string? search, int? groupId, string? kind, int? page, int? size,
        CancellationToken ct = default)
    {
        var visible = p.GroupsWith(Perm.Ro);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Media.AsNoTracking().Where(m => visible.Contains(m.GroupId));
        if (groupId is { } g) q = q.Where(m => m.GroupId == g);
        if (kind == "image") q = q.Where(m => m.ContentType.StartsWith("image/"));
        else if (kind == "pdf") q = q.Where(m => m.ContentType == "application/pdf");
        if (!string.IsNullOrWhiteSpace(search)) { var like = Paging.Like(search); q = q.Where(m => EF.Functions.ILike(m.FileName, like)); }
        return await q.OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).ToPageAsync(page, size, ct);
    }

    public async Task<MediaItem> UploadAsync(EffectivePermissions p, int groupId, string fileName, Stream content, string actor,
        CancellationToken ct = default)
    {
        if (!p.Has(groupId, Perm.Create)) throw new AccessDeniedException("You cannot upload into that group.");
        Directory.CreateDirectory(paths.Value.MediaDirectory);
        var head = new byte[16];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        var sniffed = Sniff(head.AsSpan(0, read)) ?? throw new Blocks.BlockException("Only PNG, JPEG, GIF, WebP, AVIF and ICO images and PDF files can be uploaded.");
        var stored = Guid.NewGuid().ToString("N") + sniffed.Ext;
        var path = Path.Combine(paths.Value.MediaDirectory, stored);
        long size = read;
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await fs.WriteAsync(head.AsMemory(0, read), ct);
            var buffer = new byte[81920];
            int n;
            while ((n = await content.ReadAsync(buffer, ct)) > 0)
            {
                size += n;
                if (size > MaxBytes)
                {
                    await fs.DisposeAsync();
                    File.Delete(path);
                    throw new Blocks.BlockException($"Files can be at most {MaxBytes / (1024 * 1024)} MiB.");
                }
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 255) safeName = safeName[^255..];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = new MediaItem
        {
            GroupId = groupId, FileName = safeName, StoredName = stored, ContentType = sniffed.Type, Size = size,
            UploadedById = p.UserId == 0 ? null : p.UserId, CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        db.Media.Add(item);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(actor, "media.upload", "Media", item.Id.ToString(), safeName, ct: ct);
        return item;
    }

    public async Task DeleteAsync(EffectivePermissions p, int id, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var m = await db.Media.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        if (!p.Has(m.GroupId, Perm.Ro)) throw new NotFoundException();
        var own = m.UploadedById == p.UserId && p.Has(m.GroupId, Perm.Create);
        if (!p.Has(m.GroupId, Perm.Delete) && !own) throw new AccessDeniedException();
        db.Media.Remove(m);
        await db.SaveChangesAsync(ct);
        try { File.Delete(Path.Combine(paths.Value.MediaDirectory, m.StoredName)); } catch (IOException) { }
        await audit.WriteAsync(actor, "media.delete", "Media", id.ToString(), m.FileName, ct: ct);
    }

    /// <summary>Path of a stored file, or null; the name must be exactly one of ours (no traversal).</summary>
    public string? ResolveFile(string storedName)
    {
        if (storedName.Length is < 33 or > 40 || !storedName[..32].All(char.IsAsciiHexDigitLower) || storedName[32] != '.') return null;
        var path = Path.Combine(paths.Value.MediaDirectory, storedName);
        return File.Exists(path) ? path : null;
    }
}

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

    public const long MaxSvgBytes = 1024 * 1024;

    /// <summary>
    /// Whether an SVG file is safe to serve: well-formed XML (no DTD), an svg root, no scripts, event handlers,
    /// embedded HTML or frames, and links only to parts of the same file. Null when safe, else the reason.
    /// </summary>
    public static string? WhyUnsafeSvg(byte[] data)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(new MemoryStream(data), new System.Xml.XmlReaderSettings
            {
                // A DOCTYPE (editors write one) is skipped; entities it declares are then unknown, which fails the parse.
                DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = MaxSvgBytes * 2,
            });
            var root = true;
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.ProcessingInstruction && reader.Name != "xml") return "processing instructions are not allowed";
                if (reader.NodeType == System.Xml.XmlNodeType.CDATA && reader.Value.Contains("<", StringComparison.Ordinal)) return "markup in CDATA is not allowed";
                if (reader.NodeType == System.Xml.XmlNodeType.Text && reader.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) return "scripts are not allowed";
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                var name = reader.LocalName.ToLowerInvariant();
                if (root)
                {
                    if (name != "svg" || reader.NamespaceURI != "http://www.w3.org/2000/svg") return "the file is not an SVG image";
                    root = false;
                }
                if (name is "script" or "foreignobject" or "iframe" or "embed" or "object" or "audio" or "video" or "handler" or "listener")
                    return $"<{reader.LocalName}> elements are not allowed";
                // HTML and MathML could render markup; other namespaces (Inkscape, Sodipodi, RDF metadata) are inert in SVG.
                if (reader.NamespaceURI is "http://www.w3.org/1999/xhtml" or "http://www.w3.org/1998/Math/MathML") return "HTML inside SVG is not allowed";
                if (name is "animate" or "set" or "animatemotion" or "animatetransform"
                    && reader.GetAttribute("attributeName") is { } animated && animated.Contains("href", StringComparison.OrdinalIgnoreCase))
                    return "animating links is not allowed";
                if (name == "style" && !reader.IsEmptyElement)
                {
                    var css = reader.ReadElementContentAsString();
                    if (UnsafeCss(css)) return "styles may not load other files";
                    continue;
                }
                for (var i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToAttribute(i);
                    var attr = reader.LocalName.ToLowerInvariant();
                    var value = reader.Value.Trim();
                    if (attr.StartsWith("on", StringComparison.Ordinal)) return "event handlers are not allowed";
                    var compact = new string(value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
                    if (compact.Contains("javascript:", StringComparison.OrdinalIgnoreCase) || compact.Contains("data:text/html", StringComparison.OrdinalIgnoreCase))
                        return "scripts are not allowed";
                    if (attr == "href" && value.Length > 0 && !value.StartsWith('#')) return "links to other files are not allowed";
                    if ((attr == "style" || value.Contains("url(", StringComparison.OrdinalIgnoreCase)) && UnsafeCss(value)) return "styles may not load other files";
                }
                reader.MoveToElement();
            }
            return root ? "the file is not an SVG image" : null;
        }
        catch (System.Xml.XmlException) { return "the file is not well-formed XML"; }
    }

    private static bool UnsafeCss(string css) =>
        css.Contains("@import", StringComparison.OrdinalIgnoreCase) || css.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(css, @"url\(\s*(?!['""]?\s*#)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Text files that start with "&lt;" (after a byte-order mark and white space) may be SVG.</summary>
    private static bool MaybeSvg(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) head = head[3..];
        var i = 0;
        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < head.Length && head[i] == (byte)'<';
    }

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
        await using (var check = await dbFactory.CreateDbContextAsync(ct))
            if (!await check.Groups.AnyAsync(g => g.Id == groupId && g.Valid && !g.IsSystem, ct))
                throw new AccessDeniedException("Files cannot be uploaded into that group.");
        Directory.CreateDirectory(paths.Value.MediaDirectory);
        var head = new byte[16];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        var sniffed = Sniff(head.AsSpan(0, read));
        if (sniffed is null && MaybeSvg(head.AsSpan(0, read)))
        {
            // SVG: read it whole (at most 1 MiB) and check it before it is stored.
            using var all = new MemoryStream();
            all.Write(head, 0, read);
            var buf = new byte[81920];
            int got;
            while ((got = await content.ReadAsync(buf, ct)) > 0)
            {
                all.Write(buf, 0, got);
                if (all.Length > MaxSvgBytes) throw new Blocks.BlockException("SVG files can be at most 1 MiB.");
            }
            var bytes = all.ToArray();
            if (WhyUnsafeSvg(bytes) is { } why) throw new Blocks.BlockException($"This SVG cannot be used: {why}.");
            head = bytes;
            read = bytes.Length;
            content = Stream.Null;
            sniffed = ("image/svg+xml", ".svg");
        }
        if (sniffed is null) throw new Blocks.BlockException("Only PNG, JPEG, GIF, WebP, AVIF, ICO and SVG images and PDF files can be uploaded.");
        var stored = Guid.NewGuid().ToString("N") + sniffed.Value.Ext;
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
            GroupId = groupId, FileName = safeName, StoredName = stored, ContentType = sniffed.Value.Type, Size = size,
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
        // Like own unpublished content: deleting one's own upload without delete is fine until it is used publicly.
        if (!p.Has(m.GroupId, Perm.Delete) && await IsUsedPubliclyAsync(db, id, ct))
            throw new AccessDeniedException("The file is used on published pages or as the logo or favicon; deleting it needs delete.");
        db.Media.Remove(m);
        await db.SaveChangesAsync(ct);
        try { File.Delete(Path.Combine(paths.Value.MediaDirectory, m.StoredName)); } catch (IOException) { }
        await audit.WriteAsync(actor, "media.delete", "Media", id.ToString(), m.FileName, ct: ct);
    }

    private static async Task<bool> IsUsedPubliclyAsync(AppDbContext db, int id, CancellationToken ct)
    {
        var used = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM content_items
            WHERE "Status" = 1 AND "Blocks" IS NOT NULL
              AND jsonb_path_exists("Blocks", '$.** ? (@.mediaId == $id)', jsonb_build_object('id', {id}))
            """).SingleAsync(ct);
        if (used > 0) return true;
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (await db.Settings.AnyAsync(s => (s.Key == SiteSettings.LogoKey || s.Key == SiteSettings.FaviconKey || s.Key == SiteSettings.FaviconSvgKey
                                             || s.Key == SiteSettings.FaviconIcoKey) && s.Value == key, ct)) return true;
        return await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM themes
            WHERE jsonb_path_exists("HeaderBlocks", '$.** ? (@.mediaId == $id)', jsonb_build_object('id', {id}))
               OR jsonb_path_exists("FooterBlocks", '$.** ? (@.mediaId == $id)', jsonb_build_object('id', {id}))
            """).SingleAsync(ct) > 0;
    }

    /// <summary>Path of a stored file, or null; the name must be exactly one of ours (no traversal).</summary>
    public string? ResolveFile(string storedName)
    {
        if (storedName.Length is < 33 or > 40 || !storedName[..32].All(char.IsAsciiHexDigitLower) || storedName[32] != '.') return null;
        var path = Path.Combine(paths.Value.MediaDirectory, storedName);
        return File.Exists(path) ? path : null;
    }
}

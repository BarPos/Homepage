// Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Homepage.Updates;

/// <summary>Parser for <c>SHA256SUMS.txt</c> (<c>sha256sum</c> output: <c>&lt;hex&gt;  &lt;name&gt;</c> or <c>&lt;hex&gt; *&lt;name&gt;</c>).</summary>
public static partial class Sha256Sums
{
    public const int MaxBytes = 64 * 1024;

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = LineRegex().Match(line);
            if (!m.Success) continue;
            var name = m.Groups[2].Value;
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            result[name] = m.Groups[1].Value.ToLowerInvariant();
        }
        return result;
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64}) [ *](\S.*)$")]
    private static partial Regex LineRegex();
}

internal static class FileHashing
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a new file at <paramref name="destination"/>, hashing on the way and
    /// enforcing <paramref name="maxBytes"/> while copying (the stream need not be seekable).
    /// Returns (size, sha256 lowercase hex, first 8 bytes). Deletes the file on failure.
    /// </summary>
    public static async Task<(long Size, string Sha256, byte[] Head)> CopyAndHashAsync(Stream source, string destination,
        long maxBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new byte[8];
        var headLength = 0;
        long total = 0;
        try
        {
            await using (var fs = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > maxBytes) throw new InvalidDataException($"The file is larger than {maxBytes / (1024 * 1024)} MiB.");
                    if (headLength < head.Length)
                    {
                        var take = Math.Min(head.Length - headLength, n);
                        Array.Copy(buffer, 0, head, headLength, take);
                        headLength += take;
                    }
                    hash.AppendData(buffer, 0, n);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                await fs.FlushAsync(ct);
            }
            return (total, Convert.ToHexStringLower(hash.GetHashAndReset()), head[..headLength]);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public static async Task<string> ReadSmallTextAsync(Stream source, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > maxBytes) throw new InvalidDataException("The file is too large.");
            ms.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void TryDeleteDirectory(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Files shared with <c>deploy/linux/update.sh</c>. Both live in <c>Paths:Data/updates/</c>.
/// Format: UTF-8 text, one <c>key=value</c> per line, <c>#</c> comments; update.sh parses it without eval and
/// validates every value. The server writes the request atomically (temp file + rename) because the systemd path
/// unit <c>homepage-update.path</c> fires as soon as <c>request.conf</c> exists.
/// <code>
/// request.conf (server → update.sh)      result.conf (update.sh → server)
///   format=1                               format=1
///   run_id=&lt;guid&gt;                         run_id=&lt;guid&gt;
///   version=1.2.0                          version=1.2.0
///   from_version=1.1.0                     from_version=1.1.0
///   arch=x64                               state=Succeeded|RolledBack|Failed
///   file=Homepage.Server-1.2.0-linux-x64.tar.gz   message=&lt;one line&gt;
///   sha256=&lt;64 hex&gt;                       finished_at=2026-01-01T00:00:00Z
/// </code>
/// The package and its SHA256SUMS.txt are in <c>updates/&lt;version&gt;/</c>.
/// </summary>
public static class UpdateRequestFile
{
    public const string RequestFileName = "request.conf";
    public const string ResultFileName = "result.conf";
    public const int Format = 1;

    public static string BuildRequest(Guid runId, string version, string fromVersion, string arch, string file, string sha256)
    {
        var sb = new StringBuilder();
        sb.Append("# Homepage update request. Written by the server, applied by update.sh.\n");
        sb.Append("format=").Append(Format.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("run_id=").Append(runId.ToString("D")).Append('\n');
        sb.Append("version=").Append(version).Append('\n');
        sb.Append("from_version=").Append(fromVersion).Append('\n');
        sb.Append("arch=").Append(arch).Append('\n');
        sb.Append("file=").Append(file).Append('\n');
        sb.Append("sha256=").Append(sha256).Append('\n');
        return sb.ToString();
    }

    public static Dictionary<string, string> ParseKeyValues(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = line[(eq + 1)..];
        }
        return result;
    }
}

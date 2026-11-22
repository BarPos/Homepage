using System.Net;
using System.Net.Sockets;

namespace Homepage.Web.Rendering;

/// <summary>
/// Limits failed sign-ins without locking accounts (a lockout lets anyone keep an admin out with five wrong guesses).
/// Within 15 minutes: 5 failures per account from one network, 20 per network, 50 per account from all networks.
/// A slot is reserved before the password is checked, so parallel guesses cannot get more tries than the limit.
/// IPv6 clients count per /64, the smallest block a user normally controls.
/// </summary>
public sealed class SignInLimiter(TimeProvider time)
{
    public const int PerAccountAndNetwork = 5, PerNetwork = 20, PerAccount = 50;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public static string Network(IPAddress? ip)
    {
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();
        var b = ip.GetAddressBytes();
        Array.Clear(b, 8, 8);
        return new IPAddress(b) + "/64";
    }

    /// <summary>Reserves one attempt; null when a limit is reached. Call <see cref="Succeeded"/> to give it back.</summary>
    public string[]? TryReserve(string account, IPAddress? ip)
    {
        var net = Network(ip);
        var keys = new[] { $"a|{account}|{net}", $"n|{net}", $"a|{account}" };
        var limits = new[] { PerAccountAndNetwork, PerNetwork, PerAccount };
        var now = time.GetUtcNow();
        lock (_lock)
        {
            for (var i = 0; i < keys.Length; i++)
                if (Recent(keys[i], now).Count >= limits[i]) return null;
            foreach (var k in keys) Recent(k, now).Add(now);
            return keys;
        }
    }

    /// <summary>A correct password: the reserved attempt does not count.</summary>
    public void Succeeded(string[] reservation)
    {
        lock (_lock)
            foreach (var k in reservation)
                if (_failures.TryGetValue(k, out var list) && list.Count > 0) list.RemoveAt(list.Count - 1);
    }

    private List<DateTimeOffset> Recent(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var list)) _failures[key] = list = [];
        list.RemoveAll(t => now - t > Window);
        if (_failures.Count > 100_000) foreach (var k in _failures.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _failures.Remove(k);
        return list;
    }
}

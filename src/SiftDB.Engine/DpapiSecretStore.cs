using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SiftDB.Engine;

/// <summary>
/// Saved passwords, each encrypted with DPAPI for the current Windows user (design section 8), in a file of their own
/// beside the connections. Another user, or a copy of the file on another machine, cannot decrypt them. Decrypted
/// values are only ever held in memory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string path) : ISecretStore
{
    /// <summary>Ties the blobs to this app: another program decrypting as the same user also has to know this.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SiftDB saved connection password v1");

    private readonly object _gate = new();

    public string? Get(string id)
    {
        lock (_gate)
        {
            if (!Read().TryGetValue(id, out var blob)) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null; // another user's or another machine's secret: as good as not saved, so connecting asks again
            }
        }
    }

    public void Set(string id, string secret)
    {
        lock (_gate)
        {
            var all = Read();
            all[id] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(all));
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Remove(id)) AtomicFile.WriteAllText(path, JsonSerializer.Serialize(all));
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { return new(); } // unreadable: every connection asks for its password again
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniChain.Core;

/// <summary>Допоміжні функції хешування (SHA-256 з бібліотеки .NET).</summary>
public static class Hash
{
    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);
    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));
    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);
    public static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>
/// Гаманець: пара ключів ECDSA на кривій secp256k1.
/// Адреса = перші 20 байт SHA-256 від публічного ключа (X||Y) у hex.
/// </summary>
public sealed class Wallet
{
    public string PrivateKey { get; init; } = "";   // D, 32 байти hex — НІКОЛИ не потрапляє в репозиторій
    public string PublicKey { get; init; } = "";    // X||Y, 64 байти hex
    [JsonIgnore] public string Address => AddressOf(PublicKey);

    public static Wallet Create()
    {
        using var ec = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
        var p = ec.ExportParameters(includePrivateParameters: true);
        return new Wallet
        {
            PrivateKey = Hash.ToHex(p.D!),
            PublicKey = Hash.ToHex(p.Q.X!.Concat(p.Q.Y!).ToArray())
        };
    }

    public static string AddressOf(string publicKeyHex) =>
        Hash.Sha256Hex(Hash.FromHex(publicKeyHex))[..40];

    public string Sign(byte[] data)
    {
        using var ec = ECDsa.Create(ToParameters(PublicKey, PrivateKey));
        return Hash.ToHex(ec.SignData(data, HashAlgorithmName.SHA256));
    }

    public static bool Verify(string publicKeyHex, byte[] data, string signatureHex)
    {
        try
        {
            using var ec = ECDsa.Create(ToParameters(publicKeyHex, null));
            return ec.VerifyData(data, Hash.FromHex(signatureHex), HashAlgorithmName.SHA256);
        }
        catch (Exception) { return false; } // неправильний формат ключа чи підпису = недійсний підпис
    }

    private static ECParameters ToParameters(string pubHex, string? privHex)
    {
        var q = Hash.FromHex(pubHex);
        if (q.Length != 64) throw new ArgumentException("public key must be 64 bytes");
        return new ECParameters
        {
            Curve = ECCurve.CreateFromFriendlyName("secP256k1"),
            Q = new ECPoint { X = q[..32], Y = q[32..] },
            D = privHex is null ? null : Hash.FromHex(privHex)
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json.Pretty);
    public static Wallet FromJson(string json) => JsonSerializer.Deserialize<Wallet>(json, Json.Pretty)!;
}

/// <summary>Налаштування JSON: однакові на всіх вузлах.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    public static readonly JsonSerializerOptions Pretty = new(Compact) { WriteIndented = true };

    /// <summary>
    /// Детермінована серіалізація: ключі сортуються за абеткою (ordinal), без пробілів.
    /// Ті самі дані завжди дають ті самі байти, тому підпис з одного вузла перевіряється на іншому.
    /// </summary>
    public static byte[] Canonical(IDictionary<string, object> fields)
    {
        var sorted = new SortedDictionary<string, object>(fields, StringComparer.Ordinal);
        return JsonSerializer.SerializeToUtf8Bytes(sorted, Compact);
    }
}

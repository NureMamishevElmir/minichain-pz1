using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MiniChain.Core;

/// <summary>Заголовок блоку. Хеш блоку — SHA-256 від детермінованої серіалізації заголовка.</summary>
public sealed class BlockHeader
{
    public long Index { get; set; }                  // номер блоку (висота)
    public long Timestamp { get; set; }              // мітка часу, Unix мс
    public string PrevHash { get; set; } = "";       // хеш попереднього блоку
    public string MerkleRoot { get; set; } = "";     // корінь Меркла транзакцій
    public int Difficulty { get; set; }              // складність: кількість нульових БІТІВ на початку хешу
    public long Nonce { get; set; }                  // число, яке підбирає майнер

    /// <summary>
    /// Детермінована серіалізація: поля у фіксованому порядку через «|», nonce останнім.
    /// Nonce в кінці дозволяє майнеру один раз підготувати префікс і дописувати лише число.
    /// </summary>
    public string Prefix() => $"{Index}|{Timestamp}|{PrevHash}|{MerkleRoot}|{Difficulty}|";
    public byte[] Serialize() => Encoding.ASCII.GetBytes(Prefix() + Nonce);
    public string ComputeHash() => Hash.Sha256Hex(Serialize());
    public BlockHeader Clone() => (BlockHeader)MemberwiseClone();
}

public sealed class Block
{
    public BlockHeader Header { get; set; } = new();
    public List<Transaction> Transactions { get; set; } = new();
    public string Hash { get; set; } = "";           // зберігається для зручності, але при перевірці перераховується

    [JsonIgnore] public long Index => Header.Index;
    [JsonIgnore] public int Size => Transactions.Sum(t => t.Size);

    public string ComputeMerkleRoot() => Merkle.Root(Transactions.Select(t => t.Id).ToList());

    /// <summary>Робота блоку = очікувана кількість спроб хешування = 2^difficulty.</summary>
    [JsonIgnore] public double Work => Math.Pow(2, Header.Difficulty);

    public Block Clone() => new()
    {
        Header = Header.Clone(),
        Transactions = Transactions.Select(t => t.Clone()).ToList(),
        Hash = Hash
    };
}

/// <summary>Proof of Work.</summary>
public static class ProofOfWork
{
    /// <summary>Хеш має починатися щонайменше з difficulty нульових бітів, тобто hash &lt; 2^(256-difficulty).</summary>
    public static bool Meets(string hashHex, int difficulty) => LeadingZeroBits(Core.Hash.FromHex(hashHex)) >= difficulty;

    public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        int bits = 0;
        foreach (var b in hash)
        {
            if (b == 0) { bits += 8; continue; }
            bits += System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24;
            break;
        }
        return bits;
    }

    /// <summary>
    /// Перебирає nonce, доки хеш заголовка не задовольнить складність.
    /// Повертає кількість спроб. Префікс заголовка кодується один раз — це в рази швидше.
    /// </summary>
    public static long Mine(BlockHeader header, CancellationToken ct = default)
    {
        var prefix = Encoding.ASCII.GetBytes(header.Prefix());
        var buf = new byte[prefix.Length + 20];
        prefix.CopyTo(buf, 0);
        Span<byte> hash = stackalloc byte[32];
        long attempts = 0;
        for (long nonce = 0; ; nonce++)
        {
            if ((nonce & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            Utf8Formatter.TryFormat(nonce, buf.AsSpan(prefix.Length), out int written);
            SHA256.HashData(buf.AsSpan(0, prefix.Length + written), hash);
            attempts++;
            if (LeadingZeroBits(hash) >= header.Difficulty)
            {
                header.Nonce = nonce;
                return attempts;
            }
        }
    }
}

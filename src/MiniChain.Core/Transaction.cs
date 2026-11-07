using System.Text.Json.Serialization;

namespace MiniChain.Core;

/// <summary>
/// Транзакція в моделі рахунків. Підписуються всі поля, крім самого підпису:
/// from, to, amount, fee, nonce, publicKey і chainId. Тому підпис не можна
/// «переклеїти» на іншу транзакцію — зміна будь-якого поля змінює підписувані байти.
/// </summary>
public sealed class Transaction
{
    public const string CoinbaseSender = "COINBASE";

    public string From { get; set; } = "";       // адреса відправника (hash публічного ключа)
    public string To { get; set; } = "";         // адреса отримувача
    public long Amount { get; set; }             // сума в монетах
    public long Fee { get; set; }                // комісія майнеру
    public long Nonce { get; set; }              // порядковий номер транзакції відправника (0, 1, 2...)
    public string PublicKey { get; set; } = "";  // потрібен, щоб перевірити підпис і адресу
    public string Signature { get; set; } = "";  // ECDSA secp256k1 (r||s) у hex

    [JsonIgnore] public bool IsCoinbase => From == CoinbaseSender;

    /// <summary>Ідентифікатор = SHA-256 від підписуваних байтів (без підпису, тож не залежить від його форми).</summary>
    [JsonIgnore] public string Id => Hash.Sha256Hex(SigningBytes());

    /// <summary>Детерміновані байти для підпису: канонічний JSON з відсортованими ключами.</summary>
    public byte[] SigningBytes() => Json.Canonical(new Dictionary<string, object>
    {
        ["chainId"] = ChainParams.ChainId,
        ["from"] = From,
        ["to"] = To,
        ["amount"] = Amount,
        ["fee"] = Fee,
        ["nonce"] = Nonce,
        ["publicKey"] = PublicKey
    });

    /// <summary>Розмір транзакції в байтах (для ліміту розміру блоку).</summary>
    [JsonIgnore] public int Size => SigningBytes().Length + Signature.Length / 2;

    public static Transaction Create(Wallet sender, string to, long amount, long fee, long nonce)
    {
        var tx = new Transaction
        {
            From = sender.Address, To = to, Amount = amount, Fee = fee, Nonce = nonce, PublicKey = sender.PublicKey
        };
        tx.Signature = sender.Sign(tx.SigningBytes());
        return tx;
    }

    /// <summary>Винагорода майнеру. Nonce = номер блоку, щоб coinbase різних блоків мали різні id.</summary>
    public static Transaction Coinbase(string miner, long amount, long height) =>
        new() { From = CoinbaseSender, To = miner, Amount = amount, Fee = 0, Nonce = height };

    /// <summary>Перевірка підпису і того, що адреса відправника справді належить цьому ключу.</summary>
    public string? CheckSignature()
    {
        if (IsCoinbase) return "coinbase-транзакція не може бути поза першою позицією блоку";
        if (string.IsNullOrEmpty(PublicKey) || Wallet.AddressOf(PublicKey) != From)
            return "адреса відправника не відповідає публічному ключу";
        if (!Wallet.Verify(PublicKey, SigningBytes(), Signature))
            return "недійсний підпис";
        return null;
    }

    public Transaction Clone() => (Transaction)MemberwiseClone();
    public override string ToString() => $"{Short(From)} -> {Short(To)}: {Amount} (fee {Fee}, nonce {Nonce})";
    public static string Short(string s) => s.Length > 10 ? s[..8] + ".." : s;
}

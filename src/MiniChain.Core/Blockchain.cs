using System.Text.Json;

namespace MiniChain.Core;

/// <summary>Результат повної перевірки ланцюга: список усіх знайдених порушень.</summary>
public sealed class ValidationReport
{
    public List<string> Errors { get; } = new();
    public bool IsValid => Errors.Count == 0;
    public override string ToString() => IsValid ? "Ланцюг коректний" : string.Join(Environment.NewLine, Errors);
}

/// <summary>
/// Ланцюг блоків і стан рахунків (баланси та наступні nonce), отриманий програванням усіх блоків.
/// Будь-який блок додається лише після повної перевірки за правилами консенсусу.
/// </summary>
public sealed class Blockchain
{
    public ChainParams Params { get; }
    private readonly List<Block> _blocks = new();
    private readonly Dictionary<string, long> _balances = new();
    private readonly Dictionary<string, long> _nextNonce = new();
    private readonly HashSet<string> _txIds = new();

    public Blockchain(ChainParams? p = null)
    {
        Params = p ?? ChainParams.Default;
        _blocks.Add(Genesis(Params));
    }

    public IReadOnlyList<Block> Blocks => _blocks;
    public Block Tip => _blocks[^1];
    public long Height => Tip.Index;
    public double TotalWork => _blocks.Skip(1).Sum(b => b.Work);   // генезис однаковий у всіх, не рахуємо
    public long Balance(string address) => _balances.GetValueOrDefault(address);
    public long NextNonce(string address) => _nextNonce.GetValueOrDefault(address);
    public bool ContainsTx(string txId) => _txIds.Contains(txId);
    public IReadOnlyDictionary<string, long> Balances => _balances;

    /// <summary>Генезис-блок: детермінований, тому однаковий на всіх вузлах.</summary>
    public static Block Genesis(ChainParams p)
    {
        var header = new BlockHeader
        {
            Index = 0, Timestamp = p.GenesisTimestamp, PrevHash = new string('0', 64),
            MerkleRoot = Merkle.EmptyRoot, Difficulty = p.InitialDifficulty, Nonce = 0
        };
        return new Block { Header = header, Hash = header.ComputeHash() };
    }

    // ---------------------------------------------------------------- складність

    /// <summary>
    /// Складність для блоку з номером height. Кожні K блоків дивимось, скільки часу пішло
    /// на останні K-1 інтервалів, і зсуваємо складність на log2(очікуваний/фактичний) біт.
    /// +1 біт = удвічі більше роботи, тож це пропорційне коригування.
    /// </summary>
    public static int ExpectedDifficulty(IReadOnlyList<Block> blocks, long height, ChainParams p)
    {
        if (height == 0) return p.InitialDifficulty;
        int prev = blocks[(int)height - 1].Header.Difficulty;
        int k = p.AdjustInterval;
        if (height % k != 0 || height - k < 1) return prev;   // генезис має штучну мітку часу — не враховуємо
        long actual = blocks[(int)height - 1].Header.Timestamp - blocks[(int)height - k].Header.Timestamp;
        long expected = (k - 1) * p.TargetBlockMs;
        double ratio = (double)expected / Math.Max(actual, 1);
        int delta = (int)Math.Round(Math.Log2(ratio));
        delta = Math.Clamp(delta, -p.MaxAdjustStep, p.MaxAdjustStep);
        return Math.Clamp(prev + delta, p.MinDifficulty, p.MaxDifficulty);
    }

    public int NextDifficulty() => ExpectedDifficulty(_blocks, Height + 1, Params);

    // ---------------------------------------------------------------- створення блоку

    /// <summary>Готує блок-кандидат: coinbase (винагорода + комісії) першою, далі вибрані транзакції.</summary>
    public Block CreateCandidate(string minerAddress, IEnumerable<Transaction> txs, long? timestamp = null)
    {
        var list = txs.ToList();
        long fees = list.Sum(t => t.Fee);
        long height = Height + 1;
        var all = new List<Transaction> { Transaction.Coinbase(minerAddress, Params.BlockReward + fees, height) };
        all.AddRange(list);
        var block = new Block
        {
            Header = new BlockHeader
            {
                Index = height,
                Timestamp = Math.Max(timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Tip.Header.Timestamp + 1),
                PrevHash = Tip.Hash,
                Difficulty = NextDifficulty()
            },
            Transactions = all
        };
        block.Header.MerkleRoot = block.ComputeMerkleRoot();
        return block;
    }

    // ---------------------------------------------------------------- перевірка і додавання

    /// <summary>Перевіряє блок як наступний після поточної вершини і, якщо все гаразд, додає його.</summary>
    public string? TryAddBlock(Block block, long? now = null)
    {
        var error = CheckHeader(block, _blocks, Params, now) ?? CheckTransactions(block, apply: true);
        if (error is null) _blocks.Add(block);
        return error;
    }

    /// <summary>Перевірки заголовка: висота, зв'язок, хеш, PoW, складність, час, Меркл, розмір.</summary>
    private static string? CheckHeader(Block block, IReadOnlyList<Block> prevBlocks, ChainParams p, long? now)
    {
        var h = block.Header;
        var prev = prevBlocks[^1];
        if (h.Index != prev.Index + 1) return $"неправильний номер блоку {h.Index}, очікувався {prev.Index + 1}";
        if (h.PrevHash != prev.Hash) return "посилання на попередній блок не збігається з його хешем";
        if (h.ComputeHash() != block.Hash) return "збережений хеш блоку не відповідає заголовку";
        if (!ProofOfWork.Meets(block.Hash, h.Difficulty)) return "хеш не відповідає складності (немає PoW)";
        int expected = ExpectedDifficulty(prevBlocks, h.Index, p);
        if (h.Difficulty != expected) return $"складність {h.Difficulty}, а за правилами має бути {expected}";
        if (h.Timestamp < prev.Header.Timestamp) return "мітка часу раніша за попередній блок";
        if (h.Timestamp > (now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + p.MaxFutureDriftMs)
            return "мітка часу занадто далеко в майбутньому";
        if (block.Transactions.Count == 0 || !block.Transactions[0].IsCoinbase) return "першою має бути coinbase-транзакція";
        if (h.MerkleRoot != block.ComputeMerkleRoot()) return "корінь Меркла не збігається з транзакціями";
        if (block.Transactions.Skip(1).Sum(t => t.Size) > p.MaxBlockBytes) return "перевищено ліміт розміру блоку";
        return null;
    }

    /// <summary>
    /// Перевірки транзакцій відносно поточного стану: підписи, nonce по порядку,
    /// баланс, дублікати, розмір винагороди. Якщо apply — оновлює стан.
    /// </summary>
    private string? CheckTransactions(Block block, bool apply)
    {
        var balances = new Dictionary<string, long>();
        var nonces = new Dictionary<string, long>();
        var seen = new HashSet<string>();
        long Bal(string a) => balances.TryGetValue(a, out var v) ? v : Balance(a);
        long Non(string a) => nonces.TryGetValue(a, out var v) ? v : NextNonce(a);

        long fees = 0;
        foreach (var (tx, i) in block.Transactions.Select((t, i) => (t, i)).Skip(1))
        {
            var where = $"транзакція #{i} ({Transaction.Short(tx.Id)})";
            if (tx.CheckSignature() is { } sigErr) return $"{where}: {sigErr}";
            if (tx.Amount <= 0 || tx.Fee < 0) return $"{where}: недопустима сума або комісія";
            if (_txIds.Contains(tx.Id) || !seen.Add(tx.Id)) return $"{where}: дублікат транзакції";
            if (tx.Nonce != Non(tx.From)) return $"{where}: nonce {tx.Nonce}, очікувався {Non(tx.From)}";
            if (Bal(tx.From) < tx.Amount + tx.Fee) return $"{where}: витрачено більше, ніж є на рахунку ({Bal(tx.From)})";
            balances[tx.From] = Bal(tx.From) - tx.Amount - tx.Fee;
            balances[tx.To] = Bal(tx.To) + tx.Amount;
            nonces[tx.From] = Non(tx.From) + 1;
            fees += tx.Fee;
        }

        var cb = block.Transactions[0];
        if (cb.Nonce != block.Index) return "coinbase: nonce має дорівнювати номеру блоку";
        if (cb.Amount > Params.BlockReward + fees)
            return $"coinbase: винагорода {cb.Amount} завищена (дозволено {Params.BlockReward + fees})";
        if (cb.Amount < 0) return "coinbase: від'ємна винагорода";
        if (block.Transactions.Skip(1).Any(t => t.IsCoinbase)) return "coinbase-транзакція не на першій позиції";

        if (!apply) return null;
        balances[cb.To] = Bal(cb.To) + cb.Amount;
        foreach (var (a, v) in balances) _balances[a] = v;
        foreach (var (a, v) in nonces) _nextNonce[a] = v;
        foreach (var t in block.Transactions) _txIds.Add(t.Id);
        return null;
    }

    /// <summary>
    /// Повна перевірка ланцюга. Структурні перевірки (хеш, зв'язок, PoW, Меркл) виконуються для
    /// КОЖНОГО блоку, щоб показати всі місця порушення; перевірка стану (підписи, баланси, nonce,
    /// винагорода) програє блоки по порядку і зупиняється на першій помилці.
    /// </summary>
    public static ValidationReport Validate(IReadOnlyList<Block> blocks, ChainParams p, long? now = null)
    {
        var report = new ValidationReport();
        if (blocks.Count == 0) { report.Errors.Add("порожній ланцюг"); return report; }
        var genesis = Genesis(p);
        if (blocks[0].Hash != genesis.Hash || blocks[0].Header.ComputeHash() != genesis.Hash || blocks[0].Transactions.Count != 0)
            report.Errors.Add("Блок #0: генезис-блок відрізняється від еталонного");

        var prefix = new List<Block> { blocks[0] };
        for (int i = 1; i < blocks.Count; prefix.Add(blocks[i]), i++)
        {
            if (CheckHeader(blocks[i], prefix, p, now) is { } err)
                report.Errors.Add($"Блок #{i}: {err}");
        }

        var chain = new Blockchain(p);
        for (int i = 1; i < blocks.Count; i++)
        {
            // стан перевіряємо на «чесному» префіксі; заголовок уже перевірено вище
            var err = chain.CheckTransactions(blocks[i], apply: true);
            if (err is not null) { report.Errors.Add($"Блок #{i}: {err}"); break; }
            chain._blocks.Add(blocks[i]);
        }
        return report;
    }

    /// <summary>Будує ланцюг з набору блоків з повною перевіркою (наприклад, отриманого від сусіда).</summary>
    public static Blockchain? FromBlocks(IReadOnlyList<Block> blocks, ChainParams p, out ValidationReport report)
    {
        report = Validate(blocks, p);
        if (!report.IsValid) return null;
        var chain = new Blockchain(p);
        foreach (var b in blocks.Skip(1))
            if (chain.TryAddBlock(b) is { } e) { report.Errors.Add(e); return null; }
        return chain;
    }

    // ---------------------------------------------------------------- збереження

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_blocks, Json.Pretty));
        File.Move(tmp, path, overwrite: true);   // атомарна заміна: файл не залишиться «наполовину записаним»
    }

    public static List<Block> LoadBlocks(string path) =>
        JsonSerializer.Deserialize<List<Block>>(File.ReadAllText(path), Json.Pretty) ?? new();
}

namespace MiniChain.Core;

public enum BlockResult { Added, Known, NeedSync, Invalid }

/// <summary>Результат спроби перейти на чужий ланцюг.</summary>
public sealed record AdoptResult(bool Adopted, string Message, int ReturnedToMempool = 0, int RolledBackBlocks = 0);

/// <summary>
/// Стан вузла без мережі: ланцюг + мемпул + правило вибору ланцюга.
/// Мережевий шар (MiniChain.Node) лише викликає ці методи, тому логіку можна тестувати без HTTP.
/// Усі методи потокобезпечні (один замок на вузол).
/// </summary>
public sealed class NodeState
{
    private readonly object _lock = new();
    public ChainParams Params { get; }
    public Blockchain Chain { get; private set; }
    public Mempool Mempool { get; } = new();
    public string? StoragePath { get; }

    public NodeState(ChainParams p, string? storagePath = null, Blockchain? chain = null)
    {
        Params = p;
        StoragePath = storagePath;
        Chain = chain ?? new Blockchain(p);
    }

    public T Read<T>(Func<NodeState, T> f) { lock (_lock) return f(this); }

    public string? SubmitTransaction(Transaction tx)
    {
        lock (_lock) return Mempool.TryAdd(tx, Chain);
    }

    /// <summary>Підписує і додає транзакцію від гаманця вузла: nonce береться з урахуванням мемпулу.</summary>
    public (Transaction tx, string? error) Send(Wallet from, string to, long amount, long fee)
    {
        lock (_lock)
        {
            var tx = Transaction.Create(from, to, amount, fee, Mempool.PendingNonce(from.Address, Chain));
            return (tx, Mempool.TryAdd(tx, Chain));
        }
    }

    /// <summary>
    /// Майнінг одного блоку. Перебір nonce іде поза замком; якщо за цей час вершина змінилась
    /// (прийшов чужий блок), результат відкидається.
    /// </summary>
    public (Block? block, long attempts, string? error) Mine(string minerAddress, CancellationToken ct = default)
    {
        Block candidate;
        lock (_lock)
        {
            var txs = Mempool.SelectForBlock(Chain, Params.MaxBlockBytes);
            candidate = Chain.CreateCandidate(minerAddress, txs);
        }
        long attempts = ProofOfWork.Mine(candidate.Header, ct);
        candidate.Hash = candidate.Header.ComputeHash();
        lock (_lock)
        {
            if (candidate.Header.PrevHash != Chain.Tip.Hash)
                return (null, attempts, "поки майнили, вершина ланцюга змінилась — блок застарів");
            if (Chain.TryAddBlock(candidate) is { } err) return (null, attempts, err);
            Mempool.Rebuild(Chain, Array.Empty<Transaction>());
            Persist();
            return (candidate, attempts, null);
        }
    }

    /// <summary>Блок від сусіда: додаємо, якщо він продовжує нашу вершину; інакше потрібна синхронізація.</summary>
    public (BlockResult result, string? error) ReceiveBlock(Block block)
    {
        lock (_lock)
        {
            if (Chain.Blocks.Any(b => b.Hash == block.Hash)) return (BlockResult.Known, null);
            if (block.Header.PrevHash != Chain.Tip.Hash || block.Index != Chain.Height + 1)
                return (BlockResult.NeedSync, $"блок #{block.Index} не продовжує нашу вершину #{Chain.Height}");
            if (Chain.TryAddBlock(block) is { } err) return (BlockResult.Invalid, err);
            Mempool.Rebuild(Chain, Array.Empty<Transaction>());
            Persist();
            return (BlockResult.Added, null);
        }
    }

    /// <summary>
    /// Правило вибору ланцюга: переходимо на чужий ланцюг, лише якщо він повністю коректний
    /// і в нього вкладено БІЛЬШЕ сумарної роботи (Σ 2^difficulty), ніж у наш.
    /// Транзакції з наших відкинутих блоків повертаються в мемпул.
    /// </summary>
    public AdoptResult TryAdoptChain(IReadOnlyList<Block> candidateBlocks)
    {
        var candidate = Blockchain.FromBlocks(candidateBlocks, Params, out var report);
        if (candidate is null) return new AdoptResult(false, "чужий ланцюг некоректний: " + report.Errors.FirstOrDefault());

        lock (_lock)
        {
            if (candidate.TotalWork <= Chain.TotalWork)
                return new AdoptResult(false,
                    $"чужий ланцюг не важчий: робота {candidate.TotalWork:N0} проти нашої {Chain.TotalWork:N0}");

            // спільний предок — останній блок з однаковим хешем
            int common = 0;
            while (common + 1 < Chain.Blocks.Count && common + 1 < candidate.Blocks.Count &&
                   Chain.Blocks[common + 1].Hash == candidate.Blocks[common + 1].Hash) common++;

            var orphaned = Chain.Blocks.Skip(common + 1)
                .SelectMany(b => b.Transactions.Skip(1))          // coinbase відкинутого блоку зникає
                .Where(t => !candidate.ContainsTx(t.Id)).ToList();
            int rolledBack = Chain.Blocks.Count - common - 1;

            Chain = candidate;
            Mempool.Rebuild(Chain, orphaned);
            int returned = orphaned.Count(t => Mempool.Contains(t.Id));
            Persist();
            return new AdoptResult(true,
                $"перейшли на важчий ланцюг: висота {Chain.Height}, робота {Chain.TotalWork:N0}; " +
                $"відкинуто блоків: {rolledBack}, повернуто в мемпул транзакцій: {returned}",
                returned, rolledBack);
        }
    }

    public void Persist()
    {
        if (StoragePath is not null) Chain.Save(StoragePath);
    }

    /// <summary>Завантаження з файлу після перезапуску з повною перевіркою.</summary>
    public static NodeState LoadOrCreate(ChainParams p, string path, out string message)
    {
        if (!File.Exists(path))
        {
            message = "файл ланцюга не знайдено — почато з генезис-блоку";
            var fresh = new NodeState(p, path);
            fresh.Persist();
            return fresh;
        }
        var blocks = Blockchain.LoadBlocks(path);
        var chain = Blockchain.FromBlocks(blocks, p, out var report);
        if (chain is null)
        {
            var bad = path + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(path, bad);
            message = $"ланцюг у файлі НЕКОРЕКТНИЙ ({report.Errors.First()}); файл перенесено в {Path.GetFileName(bad)}, почато з генезису";
            var fresh = new NodeState(p, path);
            fresh.Persist();
            return fresh;
        }
        message = $"ланцюг відновлено з файлу: {chain.Blocks.Count} блоків, перевірку пройдено";
        return new NodeState(p, path, chain);
    }

    /// <summary>Для демонстрації атак: зловмисник змінює дані у власній копії ланцюга (лише в пам'яті).</summary>
    public string Tamper(int height, int txIndex, long? newAmount, string? newTo, bool rehash)
    {
        lock (_lock)
        {
            var block = Chain.Blocks[height];
            var tx = block.Transactions[txIndex];
            var what = new List<string>();
            if (newAmount is { } a) { what.Add($"сума {tx.Amount} → {a}"); tx.Amount = a; }
            if (newTo is { } to) { what.Add($"отримувач {Transaction.Short(tx.To)} → {Transaction.Short(to)}"); tx.To = to; }
            if (rehash)
            {
                block.Header.MerkleRoot = block.ComputeMerkleRoot();
                ProofOfWork.Mine(block.Header);
                block.Hash = block.Header.ComputeHash();
                what.Add("корінь Меркла і хеш блоку перераховано (блок перемайнено)");
            }
            return $"Блок #{height}, транзакція #{txIndex}: " + string.Join("; ", what);
        }
    }

    /// <summary>Скасувати підробку: перечитати ланцюг з диска.</summary>
    public void ReloadFromDisk()
    {
        lock (_lock)
        {
            if (StoragePath is null) return;
            var chain = Blockchain.FromBlocks(Blockchain.LoadBlocks(StoragePath), Params, out _);
            if (chain is not null) Chain = chain;
        }
    }
}

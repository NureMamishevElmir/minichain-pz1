namespace MiniChain.Core;

/// <summary>
/// Черга непідтверджених транзакцій. Чотири перевірки перед додаванням:
/// підпис, дублікати, nonce, баланс (з урахуванням транзакцій відправника, що вже чекають у черзі).
/// </summary>
public sealed class Mempool
{
    private readonly List<Transaction> _txs = new();   // порядок надходження
    private readonly HashSet<string> _ids = new();

    public IReadOnlyList<Transaction> All => _txs;
    public int Count => _txs.Count;
    public bool Contains(string id) => _ids.Contains(id);

    /// <summary>Повертає null, якщо транзакцію прийнято, або причину відмови.</summary>
    public string? TryAdd(Transaction tx, Blockchain chain)
    {
        // 1. підпис (і що адреса відправника — це hash його публічного ключа)
        if (tx.CheckSignature() is { } sigErr) return sigErr;
        if (tx.Amount <= 0) return "сума має бути додатною";
        if (tx.Fee < 0) return "комісія не може бути від'ємною";

        // 2. дублікат у черзі (дублікат у ланцюгу відсікає вже nonce, а остаточно — перевірка нижче)
        if (_ids.Contains(tx.Id)) return "така транзакція вже є в мемпулі";

        // 3. nonce: має бути наступним після підтверджених і тих, що чекають у черзі
        long expectedNonce = PendingNonce(tx.From, chain);
        if (tx.Nonce != expectedNonce)
            return tx.Nonce < chain.NextNonce(tx.From)
                ? $"nonce {tx.Nonce} уже використано (повторне надсилання), очікувався {expectedNonce}"
                : $"неправильний nonce {tx.Nonce}, очікувався {expectedNonce}";

        // 4. баланс з урахуванням того, що відправник уже витратив у черзі
        long available = chain.Balance(tx.From) - PendingSpend(tx.From);
        // порівнюємо без додавання Amount + Fee, щоб сума long не переповнилась у від'ємне число
        if (tx.Amount > available - tx.Fee)
            return $"недостатньо коштів: доступно {available}, потрібно {(decimal)tx.Amount + tx.Fee} (можлива подвійна витрата)";
        if (chain.ContainsTx(tx.Id)) return "така транзакція вже є в ланцюгу";

        _txs.Add(tx);
        _ids.Add(tx.Id);
        return null;
    }

    public long PendingNonce(string address, Blockchain chain) =>
        chain.NextNonce(address) + _txs.Count(t => t.From == address);

    public long PendingSpend(string address) =>
        _txs.Where(t => t.From == address).Sum(t => t.Amount + t.Fee);

    /// <summary>
    /// Вибір транзакцій у блок (додаткове завдання): найбільша комісія першою,
    /// але з дотриманням порядку nonce кожного відправника і ліміту розміру блоку.
    /// </summary>
    public List<Transaction> SelectForBlock(Blockchain chain, int maxBytes)
    {
        var bySender = _txs.GroupBy(t => t.From)
            .ToDictionary(g => g.Key, g => new Queue<Transaction>(g.OrderBy(t => t.Nonce)));
        var selected = new List<Transaction>();
        int size = 0;
        var skipped = new HashSet<string>();   // відправники, чия наступна транзакція не влізла
        while (true)
        {
            var best = bySender.Where(kv => kv.Value.Count > 0 && !skipped.Contains(kv.Key))
                .Select(kv => kv.Value.Peek())
                .OrderByDescending(t => t.Fee).ThenBy(t => _txs.IndexOf(t))
                .FirstOrDefault();
            if (best is null) break;
            if (size + best.Size > maxBytes) { skipped.Add(best.From); continue; }
            selected.Add(bySender[best.From].Dequeue());
            size += best.Size;
        }
        return selected;
    }

    /// <summary>
    /// Перебудовує чергу після зміни ланцюга: прибирає підтверджені і вже недійсні транзакції,
    /// додає транзакції з відкинутих блоків (extra). Порядок — за nonce кожного відправника.
    /// </summary>
    public List<string> Rebuild(Blockchain chain, IEnumerable<Transaction> extra)
    {
        var candidates = extra.Concat(_txs).GroupBy(t => t.Id).Select(g => g.First())
            .OrderBy(t => t.From).ThenBy(t => t.Nonce).ToList();
        _txs.Clear();
        _ids.Clear();
        var dropped = new List<string>();
        foreach (var tx in candidates)
            if (!chain.ContainsTx(tx.Id) && TryAdd(tx, chain) is { } err)
                dropped.Add($"{Transaction.Short(tx.Id)}: {err}");
        return dropped;
    }
}

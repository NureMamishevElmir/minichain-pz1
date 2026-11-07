using System.Collections.Concurrent;
using System.Net.Http.Json;
using MiniChain.Core;

// ---------------------------------------------------------------- параметри запуску
// dotnet run --project src/MiniChain.Node -- --port 5001 --peers http://localhost:5002,http://localhost:5003
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var argsMap = ParseArgs(args);
int port = int.Parse(argsMap.GetValueOrDefault("port", "5001"));
string name = argsMap.GetValueOrDefault("name", $"node-{port}");
string dataDir = Path.GetFullPath(argsMap.GetValueOrDefault("data", Path.Combine("data", name)));
var chainParams = ChainParams.Default with
{
    InitialDifficulty = int.Parse(argsMap.GetValueOrDefault("difficulty", ChainParams.Default.InitialDifficulty.ToString())),
    MaxBlockBytes = int.Parse(argsMap.GetValueOrDefault("max-block-bytes", ChainParams.Default.MaxBlockBytes.ToString()))
};
Directory.CreateDirectory(Path.Combine(dataDir, "wallets"));

var node = NodeState.LoadOrCreate(chainParams, Path.Combine(dataDir, "chain.json"), out var loadMessage);
var self = $"http://localhost:{port}";
var peers = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
foreach (var p in argsMap.GetValueOrDefault("peers", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    peers[p.TrimEnd('/')] = true;
bool online = true;   // false = вузол «від'єднано» від мережі (для штучного розгалуження)
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls(self);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Compact.PropertyNamingPolicy;
    o.SerializerOptions.WriteIndented = true;
});
var app = builder.Build();

Log($"Вузол {name} на {self}; дані: {dataDir}");
Log(loadMessage);
Log($"Сусіди: {(peers.IsEmpty ? "немає" : string.Join(", ", peers.Keys))}");

// ---------------------------------------------------------------- гаманці
string WalletPath(string w) => Path.Combine(dataDir, "wallets", w + ".json");
Wallet? LoadWallet(string w) => File.Exists(WalletPath(w)) ? Wallet.FromJson(File.ReadAllText(WalletPath(w))) : null;
string ResolveAddress(string s) => LoadWallet(s)?.Address ?? s;   // ім'я гаманця або адреса

app.MapPost("/wallets", (WalletRequest r) =>
{
    if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
        return Results.BadRequest(new { error = "ім'я гаманця: літери, цифри, - або _" });
    if (File.Exists(WalletPath(r.Name))) return Results.Conflict(new { error = "гаманець з таким ім'ям уже є" });
    var w = Wallet.Create();
    File.WriteAllText(WalletPath(r.Name), w.ToJson());
    Log($"створено гаманець {r.Name}: {w.Address}");
    return Results.Ok(new { name = r.Name, address = w.Address, publicKey = w.PublicKey });
});

app.MapGet("/wallets", () => Directory.GetFiles(Path.Combine(dataDir, "wallets"), "*.json").Select(f =>
{
    var w = Wallet.FromJson(File.ReadAllText(f));
    return new { name = Path.GetFileNameWithoutExtension(f), address = w.Address, balance = node.Read(n => n.Chain.Balance(w.Address)) };
}));

// ---------------------------------------------------------------- транзакції
app.MapPost("/transactions", (SendRequest r) =>
{
    var w = LoadWallet(r.From);
    if (w is null) return Results.NotFound(new { error = $"на цьому вузлі немає гаманця {r.From}" });
    var (tx, err) = node.Send(w, ResolveAddress(r.To), r.Amount, r.Fee);
    if (err is not null) { Log($"ВІДХИЛЕНО транзакцію {tx}: {err}"); return Results.BadRequest(new { error = err, tx }); }
    Log($"прийнято транзакцію {tx}");
    _ = Broadcast("/transactions/raw", tx);
    return Results.Ok(new { id = tx.Id, tx });
});

// підписана транзакція ззовні (від сусіда, легкого клієнта або «зловмисника»)
app.MapPost("/transactions/raw", (Transaction tx) =>
{
    if (!online) return Results.StatusCode(503);
    if (node.Read(n => n.Mempool.Contains(tx.Id))) return Results.Ok(new { id = tx.Id, status = "already known" });
    var err = node.SubmitTransaction(tx);
    if (err is not null) { Log($"ВІДХИЛЕНО транзакцію {tx}: {err}"); return Results.BadRequest(new { error = err }); }
    Log($"прийнято транзакцію {tx}");
    _ = Broadcast("/transactions/raw", tx);
    return Results.Ok(new { id = tx.Id });
});

app.MapGet("/mempool", () => node.Read(n => n.Mempool.All.Select(t => new { id = t.Id, t.From, t.To, t.Amount, t.Fee, t.Nonce, size = t.Size }).ToList()));

// ---------------------------------------------------------------- майнінг і блоки
app.MapPost("/mine", (MineRequest r) =>
{
    var miner = ResolveAddress(r.Miner);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var (block, attempts, err) = node.Mine(miner);
    if (block is null) return Results.Conflict(new { error = err });
    Log($"НАМАЙНЕНО блок #{block.Index} {block.Hash[..16]}.. за {sw.Elapsed.TotalSeconds:F2} с, спроб {attempts:N0}, складність {block.Header.Difficulty}, транзакцій {block.Transactions.Count}");
    _ = Broadcast("/blocks", block);
    return Results.Ok(new { block.Index, block.Hash, block.Header.Difficulty, attempts, seconds = sw.Elapsed.TotalSeconds, transactions = block.Transactions.Count });
});

// блок від сусіда
app.MapPost("/blocks", async (Block block, HttpRequest req) =>
{
    if (!online) return Results.StatusCode(503);
    var (res, err) = node.ReceiveBlock(block);
    switch (res)
    {
        case BlockResult.Added:
            Log($"отримано блок #{block.Index} {block.Hash[..16]}.. — додано");
            _ = Broadcast("/blocks", block);
            break;
        case BlockResult.NeedSync:
            Log($"отримано блок #{block.Index}: {err} — синхронізуюсь");
            var from = req.Headers["X-Peer"].FirstOrDefault();
            await SyncWith(from is null ? peers.Keys : new[] { from });
            break;
        case BlockResult.Invalid:
            Log($"ВІДХИЛЕНО блок #{block.Index}: {err}");
            return Results.BadRequest(new { error = err });
    }
    return Results.Ok(new { result = res.ToString() });
});

app.MapGet("/chain", () => node.Read(n => n.Chain.Blocks.ToList()));
app.MapGet("/blocks/{height:int}", (int height) => node.Read(n => height >= 0 && height < n.Chain.Blocks.Count ? Results.Ok(n.Chain.Blocks[height]) : Results.NotFound()));
app.MapGet("/headers", () => node.Read(n => n.Chain.Blocks.Select(b => new { b.Hash, b.Header }).ToList()));

app.MapGet("/balance/{who}", (string who) =>
{
    var addr = ResolveAddress(who);
    return node.Read(n => new { address = addr, balance = n.Chain.Balance(addr), pendingSpend = n.Mempool.PendingSpend(addr), nextNonce = n.Chain.NextNonce(addr) });
});

app.MapGet("/validate", () =>
{
    var report = node.Read(n => Blockchain.Validate(n.Chain.Blocks, n.Params));
    Log(report.IsValid ? "перевірка ланцюга: OK" : "перевірка ланцюга: ПОМИЛКИ\n  " + string.Join("\n  ", report.Errors));
    return new { valid = report.IsValid, blocks = node.Read(n => n.Chain.Blocks.Count), errors = report.Errors };
});

// доказ Меркла для легкого клієнта (додаткове завдання)
app.MapGet("/proof/{txId}", (string txId) => node.Read(n =>
{
    foreach (var b in n.Chain.Blocks)
    {
        int i = b.Transactions.FindIndex(t => t.Id == txId);
        if (i < 0) continue;
        var ids = b.Transactions.Select(t => t.Id).ToList();
        return Results.Ok(new { txId, blockIndex = b.Index, blockHash = b.Hash, merkleRoot = b.Header.MerkleRoot, index = i, proof = Merkle.Proof(ids, i) });
    }
    return Results.NotFound(new { error = "транзакцію не знайдено в ланцюгу" });
}));

// ---------------------------------------------------------------- мережа
app.MapGet("/peers", () => peers.Keys);
app.MapPost("/peers", (PeerRequest r) =>
{
    var url = r.Url.TrimEnd('/');
    if (url.Equals(self, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "це адреса самого вузла" });
    peers[url] = true;
    Log($"додано сусіда {url}");
    return Results.Ok(peers.Keys);
});

app.MapPost("/sync", async () => Results.Ok(await SyncWith(peers.Keys)));

app.MapGet("/status", () => node.Read(n => new
{
    name, url = self, online, height = n.Chain.Height, tip = n.Chain.Tip.Hash, totalWork = n.Chain.TotalWork,
    nextDifficulty = n.Chain.NextDifficulty(), mempool = n.Mempool.Count, peers = peers.Keys
}));

// ---------------------------------------------------------------- адмін-команди для демонстрацій
app.MapPost("/admin/network", (NetworkRequest r) =>
{
    online = r.Online;
    Log(online ? "ПІДКЛЮЧЕНО до мережі" : "ВІД'ЄДНАНО від мережі (вхідні та вихідні повідомлення ігноруються)");
    return Results.Ok(new { online });
});
app.MapPost("/admin/tamper", (TamperRequest r) =>
{
    var msg = node.Tamper(r.Height, r.TxIndex, r.Amount, r.To is null ? null : ResolveAddress(r.To), r.Rehash);
    Log("ЗЛОВМИСНИК: " + msg);
    return Results.Ok(new { message = msg });
});
app.MapPost("/admin/restore", () => { node.ReloadFromDisk(); Log("ланцюг перечитано з диска"); return Results.Ok(); });

app.Run();

// ---------------------------------------------------------------- допоміжне
async Task Broadcast<T>(string path, T payload)
{
    if (!online) return;
    foreach (var peer in peers.Keys)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, peer + path) { Content = JsonContent.Create(payload, options: Json.Compact) };
            msg.Headers.Add("X-Peer", self);
            await http.SendAsync(msg);
        }
        catch (Exception e) { Log($"сусід {peer} недоступний: {e.GetType().Name}"); }
    }
}

async Task<object> SyncWith(IEnumerable<string> from)
{
    if (!online) return new { adopted = false, message = "вузол від'єднано від мережі" };
    var results = new List<string>();
    bool adopted = false;
    foreach (var peer in from.ToList())
    {
        try
        {
            var blocks = await http.GetFromJsonAsync<List<Block>>(peer + "/chain", Json.Compact);
            var res = node.TryAdoptChain(blocks!);
            adopted |= res.Adopted;
            results.Add($"{peer}: {res.Message}");
            Log($"синхронізація з {peer}: {res.Message}");
            // якщо отримали чужі транзакції назад у мемпул — розсилаємо їх
            if (res.Adopted) foreach (var tx in node.Read(n => n.Mempool.All.ToList())) _ = Broadcast("/transactions/raw", tx);
        }
        catch (Exception e) { results.Add($"{peer}: недоступний ({e.GetType().Name})"); }
    }
    var st = node.Read(n => new { n.Chain.Height, n.Chain.TotalWork, tip = n.Chain.Tip.Hash });
    return new { adopted, height = st.Height, totalWork = st.TotalWork, st.tip, results };
}

void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{name}] {msg}");

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i < a.Length; i++)
        if (a[i].StartsWith("--")) d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    return d;
}

record WalletRequest(string Name);
record SendRequest(string From, string To, long Amount, long Fee);
record MineRequest(string Miner);
record PeerRequest(string Url);
record NetworkRequest(bool Online);
record TamperRequest(int Height, int TxIndex, long? Amount, string? To, bool Rehash);

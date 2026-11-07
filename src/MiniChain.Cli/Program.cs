using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MiniChain.Core;

// Консольний клієнт вузла: mc [--node http://localhost:5001] <команда> [аргументи]
Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
var list = args.ToList();
string node = Environment.GetEnvironmentVariable("MC_NODE") ?? "http://localhost:5001";
int ni = list.IndexOf("--node");
if (ni >= 0) { node = list[ni + 1].TrimEnd('/'); list.RemoveRange(ni, 2); }
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

if (list.Count == 0) { Help(); return 0; }
string Arg(int i, string? def = null) => i < list.Count ? list[i] : def ?? throw new ArgumentException($"не вистачає аргументу #{i}");
string? Opt(string name) { int i = list.IndexOf("--" + name); return i >= 0 && i + 1 < list.Count ? list[i + 1] : null; }
bool Flag(string name) => list.Contains("--" + name);

try
{
    switch (list[0])
    {
        case "status": Print(await Get("/status")); break;
        case "wallet-create": Print(await Post("/wallets", new { name = Arg(1) })); break;
        case "wallets": Print(await Get("/wallets")); break;
        case "send": Print(await Post("/transactions", new { from = Arg(1), to = Arg(2), amount = long.Parse(Arg(3)), fee = long.Parse(Arg(4, "1")) })); break;
        case "mine": Print(await Post("/mine", new { miner = Arg(1) })); break;
        case "chain": await ShowChain(); break;
        case "block": Print(await Get($"/blocks/{Arg(1)}")); break;
        case "balance": Print(await Get($"/balance/{Arg(1)}")); break;
        case "validate": Print(await Get("/validate")); break;
        case "mempool": Print(await Get("/mempool")); break;
        case "peers": Print(await Get("/peers")); break;
        case "peer-add": Print(await Post("/peers", new { url = Arg(1) })); break;
        case "sync": Print(await Post("/sync", new { })); break;
        case "network": Print(await Post("/admin/network", new { online = Arg(1) == "on" })); break;
        case "tamper":
            Print(await Post("/admin/tamper", new
            {
                height = int.Parse(Arg(1)), txIndex = int.Parse(Arg(2)),
                amount = Opt("amount") is { } a ? long.Parse(a) : (long?)null, to = Opt("to"), rehash = Flag("rehash")
            }));
            break;
        case "restore": Print(await Post("/admin/restore", new { })); break;
        case "attack": await Attack(Arg(1)); break;
        case "proof": await LightClient(Arg(1)); break;
        default: Help(); return 1;
    }
    return 0;
}
catch (HttpRequestException e) { Error($"вузол {node} недоступний: {e.Message}"); return 2; }

// ---------------------------------------------------------------- HTTP
async Task<(int code, JsonNode? body)> Send(HttpMethod m, string path, object? body = null)
{
    var req = new HttpRequestMessage(m, node + path);
    if (body is not null) req.Content = JsonContent.Create(body, options: Json.Compact);
    var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    return ((int)res.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
}
Task<(int code, JsonNode? body)> Get(string path) => Send(HttpMethod.Get, path);
Task<(int code, JsonNode? body)> Post(string path, object body) => Send(HttpMethod.Post, path, body);

void Print((int code, JsonNode? body) r)
{
    var color = r.code < 300 ? ConsoleColor.Green : ConsoleColor.Red;
    Console.ForegroundColor = color;
    Console.WriteLine($"HTTP {r.code} {(r.code < 300 ? "OK" : "ВІДХИЛЕНО")}");
    Console.ResetColor();
    if (r.body is not null) Console.WriteLine(r.body.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
void Title(string s) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"\n=== {s} ==="); Console.ResetColor(); }
void Info(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }
void Error(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(s); Console.ResetColor(); }

async Task<List<Block>> Chain() => (await http.GetFromJsonAsync<List<Block>>(node + "/chain", Json.Compact))!;

async Task ShowChain()
{
    var blocks = await Chain();
    Console.WriteLine($"{"#",4} {"хеш",-18} {"попередній",-18} {"diff",4} {"nonce",10} {"tx",3}  час");
    foreach (var b in blocks)
        Console.WriteLine($"{b.Index,4} {b.Hash[..16] + "..",-18} {b.Header.PrevHash[..16] + "..",-18} {b.Header.Difficulty,4} {b.Header.Nonce,10} {b.Transactions.Count,3}  " +
                          DateTimeOffset.FromUnixTimeMilliseconds(b.Header.Timestamp).ToLocalTime().ToString("HH:mm:ss"));
    Console.WriteLine($"Висота: {blocks[^1].Index}, сумарна робота: {blocks.Skip(1).Sum(b => b.Work):N0}");
}

// ---------------------------------------------------------------- атаки
async Task Attack(string kind)
{
    switch (kind)
    {
        case "forged-sig":
        {
            Title("Атака: транзакція з підробленим підписом");
            var victim = Arg(2);   // адреса або ім'я гаманця жертви на вузлі
            var victimAddr = (await Get($"/balance/{victim}")).body!["address"]!.GetValue<string>();
            var bal = (await Get($"/balance/{victimAddr}")).body!;
            var attacker = Wallet.Create();
            // зловмисник знає публічний ключ жертви (він є в її попередніх транзакціях), але не приватний
            var known = (await Chain()).SelectMany(b => b.Transactions).FirstOrDefault(t => t.From == victimAddr);
            var tx = new Transaction
            {
                From = victimAddr, To = attacker.Address, Amount = bal["balance"]!.GetValue<long>(), Fee = 0,
                Nonce = bal["nextNonce"]!.GetValue<long>(), PublicKey = known?.PublicKey ?? attacker.PublicKey
            };
            tx.Signature = attacker.Sign(tx.SigningBytes());   // підписує СВОЇМ ключем
            Info($"Зловмисник {Transaction.Short(attacker.Address)} надсилає: {tx}");
            Info(known is null ? "Публічний ключ жертви невідомий — підставлено ключ зловмисника" : "Використано справжній публічний ключ жертви, підпис — ключем зловмисника");
            Print(await Post("/transactions/raw", tx));
            break;
        }
        case "double-spend":
        {
            Title("Атака: подвійна витрата (дві транзакції, кожна на весь баланс)");
            var from = Arg(2); var to1 = Arg(3); var to2 = Arg(4);
            var bal = (await Get($"/balance/{from}")).body!["balance"]!.GetValue<long>();
            Info($"Баланс {from}: {bal}. Перша транзакція: {bal - 1} + комісія 1 → {to1}");
            Print(await Post("/transactions", new { from, to = to1, amount = bal - 1, fee = 1 }));
            Info($"Друга транзакція: ті самі {bal - 1} + 1 → {to2}");
            Print(await Post("/transactions", new { from, to = to2, amount = bal - 1, fee = 1 }));
            break;
        }
        case "replay":
        {
            Title("Атака: повторне надсилання вже підтвердженої транзакції");
            var tx = (await Chain()).SelectMany(b => b.Transactions).LastOrDefault(t => !t.IsCoinbase);
            if (tx is null) { Error("у ланцюгу ще немає звичайних транзакцій"); return; }
            Info($"Повторно надсилаємо підтверджену транзакцію {Transaction.Short(tx.Id)}: {tx}");
            Print(await Post("/transactions/raw", tx));
            break;
        }
        case "tamper":
        {
            Title("Атака: зміна суми в транзакції старого блоку");
            int h = int.Parse(Arg(2)), i = int.Parse(Arg(3)); long amount = long.Parse(Arg(4));
            Print(await Post("/admin/tamper", new { height = h, txIndex = i, amount, rehash = false }));
            Info("Перевірка ланцюга після зміни:");
            Print(await Get("/validate"));
            break;
        }
        case "tamper-rehash":
        {
            Title("Атака: зміна даних із перерахунком хешу цього ж блоку");
            int h = int.Parse(Arg(2)); var to = Arg(3);
            Info($"Зловмисник переписує отримувача винагороди (coinbase) блоку #{h} на себе і перемайнює блок");
            Print(await Post("/admin/tamper", new { height = h, txIndex = 0, to, rehash = true }));
            Info("Перевірка ланцюга після зміни:");
            Print(await Get("/validate"));
            break;
        }
        default: Error("атаки: forged-sig, double-spend, replay, tamper, tamper-rehash"); break;
    }
}

// ---------------------------------------------------------------- легкий клієнт (доказ Меркла)
async Task LightClient(string txId)
{
    Title("Легкий клієнт: перевірка включення транзакції за доказом Меркла");
    var headers = (await http.GetFromJsonAsync<List<JsonElement>>(node + "/headers", Json.Compact))!;
    var hs = headers.Select(h => (hash: h.GetProperty("hash").GetString()!, header: h.GetProperty("header").Deserialize<BlockHeader>(Json.Compact)!)).ToList();
    // 1. легкий клієнт зберігає лише заголовки і сам перевіряє їхній ланцюжок і PoW
    for (int i = 0; i < hs.Count; i++)
    {
        var (hash, header) = hs[i];
        bool ok = header.ComputeHash() == hash && (i == 0 || (header.PrevHash == hs[i - 1].hash && ProofOfWork.Meets(hash, header.Difficulty)));
        if (!ok) { Error($"заголовок #{i} некоректний — вузлу не довіряємо"); return; }
    }
    int bytes = hs.Sum(h => h.header.Serialize().Length + 64);
    Info($"Отримано і перевірено {hs.Count} заголовків (~{bytes} байт; повні блоки не завантажувались)");
    // 2. доказ від повного вузла
    var (code, body) = await Get($"/proof/{txId}");
    if (code != 200) { Print((code, body)); return; }
    int idx = body!["blockIndex"]!.GetValue<int>();
    var proof = body["proof"]!.Deserialize<List<MerkleStep>>(Json.Compact)!;
    var root = hs[idx].header.MerkleRoot;   // корінь беремо з ВЛАСНОГО перевіреного заголовка, а не з відповіді вузла
    Info($"Блок #{idx}, позиція {body["index"]}, довжина доказу {proof.Count} хешів");
    foreach (var s in proof) Console.WriteLine($"  {(s.SiblingIsLeft ? "зліва " : "справа")} {s.Hash}");
    bool valid = Merkle.Verify(txId, proof, root);
    Console.ForegroundColor = valid ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine(valid ? $"ДОВЕДЕНО: транзакція входить у блок #{idx} (корінь {root[..16]}..)" : "ДОКАЗ НЕДІЙСНИЙ");
    Console.ResetColor();
    // 3. перевіримо, що підроблений доказ не проходить
    var fakeId = Hash.Sha256Hex(txId);
    Console.WriteLine($"Контроль: той самий доказ для вигаданого id {fakeId[..16]}.. → {(Merkle.Verify(fakeId, proof, root) ? "пройшов (помилка!)" : "відхилено")}");
}

void Help() => Console.WriteLine("""
Використання: mc [--node http://localhost:5001] <команда>
  status                         стан вузла
  wallet-create <ім'я>           створити гаманець (ключі зберігаються на вузлі)
  wallets                        список гаманців вузла з балансами
  send <від> <кому> <сума> [fee] надіслати транзакцію (від — ім'я гаманця, кому — ім'я або адреса)
  mine <майнер>                  намайнити блок (винагорода на гаманець або адресу)
  chain                          показати ланцюг
  block <номер>                  показати блок
  balance <ім'я|адреса>          баланс адреси
  validate                       повна перевірка ланцюга
  mempool                        черга непідтверджених транзакцій
  peers | peer-add <url>         сусіди / додати сусіда
  sync                           синхронізуватися з мережею (обрати ланцюг з найбільшою роботою)
  network on|off                 під'єднати / від'єднати вузол (для штучного розгалуження)
  tamper <блок> <tx> [--amount N] [--to X] [--rehash]   змінити дані у своїй копії ланцюга
  restore                        перечитати ланцюг з диска (скасувати підробку)
  attack forged-sig <жертва>
  attack double-spend <від> <кому1> <кому2>
  attack replay
  attack tamper <блок> <tx> <нова_сума>
  attack tamper-rehash <блок> <нова_адреса>
  proof <txId>                   легкий клієнт: перевірити доказ Меркла
""");

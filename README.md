# MiniChain — власний блокчейн з нуля (ПЗ 1)

Навчальна криптовалюта на C# (.NET 8): гаманці secp256k1, підписані транзакції, мемпул,
власне дерево Меркла, Proof of Work з автоматичним коригуванням складності, повна перевірка
ланцюга, мережа вузлів через HTTP з вибором ланцюга з найбільшою сумарною роботою.

Практичне заняття № 1 з дисципліни «Блокчейн-технології», ХНУРЕ.
Автор: Мамішев Е. І., гр. ПЗПІ-23-6.

## Вимоги

- .NET SDK 8.0 або новіший
- Windows PowerShell (скрипти) — або будь-яка ОС з командами з розділу «Вручну»

## Швидкий старт: три вузли

```powershell
.\scripts\start-nodes.ps1 -Clean       # збирає проєкт і відкриває 3 вузли (порти 5001-5003) в окремих вікнах
.\mc.cmd status                        # стан вузла 5001
```

Демонстрації (на щойно запущених вузлах з `-Clean`):

```powershell
.\scripts\demo-attacks.ps1             # п'ять атак і реакція системи
.\scripts\demo-fork.ps1                # синхронізація і розв'язання штучного розгалуження
.\scripts\stop-nodes.ps1
```

### Вручну (будь-яка ОС)

```bash
dotnet build -c Release
dotnet src/MiniChain.Node/bin/Release/net8.0/MiniChain.Node.dll --port 5001 --peers http://localhost:5002,http://localhost:5003
dotnet src/MiniChain.Node/bin/Release/net8.0/MiniChain.Node.dll --port 5002 --peers http://localhost:5001,http://localhost:5003
dotnet src/MiniChain.Node/bin/Release/net8.0/MiniChain.Node.dll --port 5003 --peers http://localhost:5001,http://localhost:5002
```

Параметри вузла: `--port`, `--peers`, `--data <тека>` (за замовчуванням `data/node-<порт>`),
`--difficulty <біт>` (початкова складність), `--max-block-bytes <байт>`.

## Керування вузлом (CLI `mc`)

| Команда | Що робить | REST |
|---|---|---|
| `mc wallet-create alice` | створити гаманець | `POST /wallets` |
| `mc wallets` | гаманці вузла з балансами | `GET /wallets` |
| `mc send alice <адреса> 10 1` | надіслати транзакцію (сума, комісія) | `POST /transactions` |
| `mc mine alice` | намайнити блок | `POST /mine` |
| `mc chain` / `mc block 3` | показати ланцюг / блок | `GET /chain`, `GET /blocks/{n}` |
| `mc balance <ім'я або адреса>` | баланс адреси | `GET /balance/{a}` |
| `mc validate` | повна перевірка ланцюга | `GET /validate` |
| `mc peer-add http://localhost:5004` | додати сусіда | `POST /peers` |
| `mc sync` | синхронізуватися з мережею | `POST /sync` |
| `mc mempool` | черга непідтверджених транзакцій | `GET /mempool` |
| `mc network off` / `on` | від'єднати вузол (штучне розгалуження) | `POST /admin/network` |
| `mc proof <txId>` | легкий клієнт: доказ Меркла | `GET /headers`, `GET /proof/{id}` |
| `mc attack ...` | атаки: `tamper`, `tamper-rehash`, `forged-sig`, `double-spend`, `replay` | |

Інший вузол: `mc --node http://localhost:5002 <команда>`.

## Тести та експеримент

```powershell
dotnet test                                                   # 22 автоматичні тести
dotnet run -c Release --project src/MiniChain.Experiment -- difficulty 10 12 16 20 24
dotnet run -c Release --project src/MiniChain.Experiment -- adjust 80 1000
dotnet run -c Release --project src/MiniChain.Experiment -- replot   # графік з наявного CSV без нового майнінгу
```

Результати (CSV і графіки) — у теці `results/`.

## Структура

```
src/MiniChain.Core        ядро: Crypto (гаманець, хеш, серіалізація), Transaction, Merkle, Block/PoW,
                          Blockchain (стан, перевірка, складність, файл), Mempool, NodeState (вибір ланцюга)
src/MiniChain.Node        HTTP-вузол (ASP.NET Core minimal API), розсилка транзакцій і блоків сусідам
src/MiniChain.Cli         консольний клієнт mc, демонстрації атак, легкий клієнт
src/MiniChain.Experiment  вимірювання часу майнінгу і коригування складності, графіки (OxyPlot)
tests/MiniChain.Tests     xUnit-тести
scripts/                  запуск мережі і демонстрації
```

## Основні рішення

- **Модель рахунків** (баланс + nonce на адресу), а не UTXO.
- **Адреса** — перші 20 байт SHA-256 від публічного ключа (64 байти X‖Y).
- **Підпис** — ECDSA secp256k1 (вбудований `System.Security.Cryptography`). Підписується канонічний JSON
  з відсортованими ключами: `amount, chainId, fee, from, nonce, publicKey, to`. Id транзакції — SHA-256 від цих байтів.
- **Заголовок блоку** серіалізується як `index|timestamp|prevHash|merkleRoot|difficulty|nonce`; nonce останній,
  тому майнер кодує префікс один раз.
- **Складність** вимірюється в нульових бітах (4 біти = 1 hex-нуль). Кожні K = 10 блоків:
  `Δ = round(log2(очікуваний час / фактичний))`, обмежено ±2.
- **Вибір ланцюга** — найбільша сумарна робота Σ 2^difficulty, а не довжина.
- **Додаткові завдання:** доказ Меркла з легким клієнтом; ліміт розміру блоку з відбором транзакцій за комісією.

## Безпека

Приватні ключі зберігаються лише локально в `data/<вузол>/wallets/` і не потрапляють у репозиторій (`.gitignore`).
Вузол — навчальний: немає TLS, автентифікації адмін-команд, захисту від DoS і атаки 51 %.

## Використання ШІ

Під час роботи використовувався ШІ-асистент Claude (Anthropic) для генерації та перевірки коду.

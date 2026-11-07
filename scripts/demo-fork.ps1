# Демонстрація синхронізації трьох вузлів і розв'язання штучного розгалуження.
# Перед запуском: .\scripts\start-nodes.ps1 -Clean
$root = Split-Path $PSScriptRoot -Parent
function mc { & "$root\mc.cmd" @args }
function step($t) { Write-Host "`n>>> $t" -ForegroundColor Cyan }
$n1 = '--node', 'http://localhost:5001'; $n3 = '--node', 'http://localhost:5003'

step '1. Гаманці: alice на вузлі 1, carol на вузлі 3'
mc @n1 wallet-create alice | Out-Null; mc @n3 wallet-create carol | Out-Null
mc @n1 wallets; mc @n3 wallets
step '2. Вузол 1 майнить 2 блоки, вузол 3 — 1 блок; усі вузли синхронні'
mc @n1 mine alice | Out-Null; mc @n1 mine alice | Out-Null; mc @n3 mine carol | Out-Null
Start-Sleep 1
foreach ($p in 5001..5003) { mc --node "http://localhost:$p" status }
step '3. Від''єднуємо вузол 3 від мережі'
mc @n3 network off
step '4. Вузол 3 (окремо) підтверджує транзакцію carol -> alice у своєму блоці #4'
mc @n3 send carol alice 10 3
mc @n3 mine carol
step '5. Тим часом вузол 1 знаходить два блоки (#4 і #5) — ланцюг розгалузився'
mc @n1 mine alice; mc @n1 mine alice
mc @n1 chain; mc @n3 chain
step '6. Під''єднуємо вузол 3 і синхронізуємо: перемагає ланцюг з більшою сумарною роботою'
mc @n3 network on
mc @n3 sync
step '7. Транзакція з відкинутого блоку повернулась у мемпул; вершини однакові'
mc @n3 mempool
mc @n3 chain
foreach ($p in 5001..5003) { mc --node "http://localhost:$p" status }

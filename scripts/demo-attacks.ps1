# Демонстрація п'яти атак. Перед запуском: .\scripts\start-nodes.ps1 -Clean
$root = Split-Path $PSScriptRoot -Parent
function mc { & "$root\mc.cmd" @args }
function step($t) { Write-Host "`n>>> $t" -ForegroundColor Cyan }
$n2 = '--node', 'http://localhost:5002'

step 'Підготовка: alice майнить 2 блоки і переказує bob 30; bob майнить блок'
mc wallet-create alice | Out-Null; mc @n2 wallet-create bob | Out-Null; mc wallet-create mallory | Out-Null
$bob = (mc @n2 balance bob | Select-String '"address"').ToString().Split('"')[3]
$mallory = (mc balance mallory | Select-String '"address"').ToString().Split('"')[3]
mc mine alice | Out-Null; mc mine alice | Out-Null
mc send alice $bob 30 2 | Out-Null; Start-Sleep 1
mc @n2 mine bob | Out-Null; Start-Sleep 1
mc chain

step 'Атака 1. Зміна суми в транзакції старого блоку (#3: 30 -> 999)'
mc attack tamper 3 1 999
mc restore | Out-Null
step 'Атака 2. Зміна даних з перерахунком хешу цього ж блоку (#2: винагорода -> mallory)'
mc attack tamper-rehash 2 $mallory
mc restore | Out-Null
step 'Атака 3. Транзакція з підробленим підписом (mallory витрачає гроші alice)'
mc attack forged-sig alice
step 'Атака 4. Подвійна витрата: bob двічі надсилає весь баланс'
mc @n2 attack double-spend bob alice mallory
step 'Атака 5. Повторне надсилання вже підтвердженої транзакції'
mc @n2 mine bob | Out-Null; Start-Sleep 1
mc attack replay

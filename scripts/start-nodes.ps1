# Запуск мережі з N вузлів (за замовчуванням 3) — кожен в окремому вікні.
#   .\scripts\start-nodes.ps1            # зібрати і запустити 3 вузли (5001-5003)
#   .\scripts\start-nodes.ps1 -Clean     # те саме, але з порожніми ланцюгами
param([switch]$Clean, [int]$Count = 3, [int]$BasePort = 5001, [int]$Difficulty = 16)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
dotnet build -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'збірка не вдалася' }
if ($Clean -and (Test-Path data)) { Remove-Item data -Recurse -Force }
$exe = Join-Path $root 'src\MiniChain.Node\bin\Release\net8.0\MiniChain.Node.exe'
$ports = 0..($Count - 1) | ForEach-Object { $BasePort + $_ }
foreach ($p in $ports) {
    $peers = ($ports | Where-Object { $_ -ne $p } | ForEach-Object { "http://localhost:$_" }) -join ','
    $cmd = "`$host.UI.RawUI.WindowTitle = 'node-$p'; & '$exe' --port $p --peers $peers --difficulty $Difficulty"
    Start-Process powershell -ArgumentList '-NoExit', '-Command', $cmd -WorkingDirectory $root
}
Write-Host "Запущено вузли: $($ports -join ', '). Керування: .\mc.cmd --node http://localhost:5001 status"

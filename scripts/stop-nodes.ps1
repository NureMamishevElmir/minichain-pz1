# Зупинити всі вузли MiniChain
Get-Process MiniChain.Node -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host 'Вузли зупинено'

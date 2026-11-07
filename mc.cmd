@echo off
rem MiniChain CLI client (build first: dotnet build -c Release)
"%~dp0src\MiniChain.Cli\bin\Release\net8.0\mc.exe" %*

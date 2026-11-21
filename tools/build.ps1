# Builds both Release flavours on Windows (requires the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

# Stop at the first failing step (PowerShell does not stop on a native command's exit code by itself).
function Invoke-Step { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE -ne 0) { throw "Failed (exit $LASTEXITCODE): $($args -join ' ')" } }

Invoke-Step dotnet test tests/G502Tool.Tests -c Release
# Self-contained: runs on any Windows 10/11 x64 PC, no .NET install needed (~146 MB folder)
Invoke-Step dotnet publish src/G502Tool.App -c Release -r win-x64 --self-contained true -o dist/G502HeroConfig
# Framework-dependent: ~1 MB, needs the ".NET Desktop Runtime 8" installed
Invoke-Step dotnet publish src/G502Tool.App -c Release -r win-x64 --self-contained false -o dist/G502HeroConfig-small
"Built: dist\G502HeroConfig\G502HeroConfig.exe and dist\G502HeroConfig-small\G502HeroConfig.exe"

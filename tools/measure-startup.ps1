# Measures time from process launch to first rendered window, N runs.
# Usage (PowerShell, from the folder that contains G502HeroConfig.exe or pass -Exe):
#   powershell -ExecutionPolicy Bypass -File measure-startup.ps1 -Exe .\G502HeroConfig.exe -Runs 5
param(
    [string]$Exe = ".\G502HeroConfig.exe",
    [int]$Runs = 5
)
$out = Join-Path $env:TEMP "g502-startup-ms.txt"
Remove-Item $out -ErrorAction SilentlyContinue
for ($i = 1; $i -le $Runs; $i++) {
    # --measure-startup: the app records ms-to-first-render and closes itself (no device I/O).
    Start-Process -FilePath $Exe -ArgumentList "--measure-startup", "`"$out`"" -Wait
}
$ms = Get-Content $out | ForEach-Object { [int]$_ }
"Runs (ms): " + ($ms -join ", ")
"First (cold-ish): $($ms[0]) ms   Median: $(($ms | Sort-Object)[[int]($ms.Count / 2)]) ms"

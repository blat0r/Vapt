# Fecha o Vapt aberto, compila a solução (x64, Debug) e abre de novo.
# Exemplo: .\scripts\run.ps1 -- --page disk --scan C:\Users
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Get-Process Vapt -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet build (Join-Path $root 'Vapt.slnx') -c Debug -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação." }

$exe = Join-Path $root 'src\Vapt.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Vapt.exe'
$args2 = @($AppArgs | Where-Object { $_ -and $_ -ne '--' })
if ($args2.Count -gt 0) { Start-Process $exe -ArgumentList $args2 } else { Start-Process $exe }

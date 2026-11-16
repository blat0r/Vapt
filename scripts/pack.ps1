# Gera o instalador do Vapt (Setup.exe para a conta do usuário e .msi para o computador todo)
# com o Velopack, como o MellowUI. O app vai autocontido (.NET e Windows App Runtime juntos):
# instala em qualquer Windows 11 sem baixar nada.
# Requer: dotnet tool install -g vpk
# Uso: .\scripts\pack.ps1 [-Runtime win-x64|win-arm64] [-Version 0.2.0]
#
# O id é "Vapt.App" de propósito: o Velopack instala em %LOCALAPPDATA%\<id> e apaga essa pasta
# ao desinstalar. Os dados do Vapt (backup das otimizações!) ficam em %LOCALAPPDATA%\Vapt e
# precisam sobreviver a uma desinstalação.
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Version,
    # No GitHub Actions não há área de trabalho para abrir a janela de verificação.
    [switch]$SkipSmoke,
    # Baixa o pacote da versão anterior do GitHub para gerar a atualização delta (só o que mudou).
    [switch]$Delta
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
}

$platform = $Runtime -replace '^win-', ''
$out = Join-Path $root "artifacts\Vapt-$Runtime"
$releases = Join-Path $root 'artifacts\releases'
$env:Path += ";$env:USERPROFILE\.dotnet\tools"

Get-Process Vapt -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
if ($Delta) {
    $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { (& gh auth token 2>$null) }
    vpk download github --repoUrl 'https://github.com/blat0r/Vapt' --token $token --outputDir $releases
    if ($LASTEXITCODE) { Write-Warning 'Sem versão anterior no GitHub: só o pacote completo.' }
}

# 1) Testes.
dotnet test (Join-Path $root 'tests\Vapt.Core.Tests') -c Release --nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# 2) App. "build -o" em vez de "publish": o publish de app WinUI não empacotado omite os
# recursos XAML compilados (.xbf/.pri) e a janela não abre. A barra no fim da pasta importa.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet build (Join-Path $root 'src\Vapt.App\Vapt.App.csproj') -c Release -r $Runtime -p:Platform=$platform -p:VaptRelease=true -p:Version=$Version --nologo -v q -o ($out + '\')
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# 3) Verificação sem janela: monta todas as páginas e sai (erro de XAML aparece aqui).
if (-not $SkipSmoke) {
    $smoke = Start-Process (Join-Path $out 'Vapt.exe') -ArgumentList '--smoke' -PassThru
    $null = $smoke.Handle # sem guardar o handle, o PowerShell 5 perde o código de saída
    if (-not $smoke.WaitForExit(120000)) { $smoke.Kill(); Write-Error 'A verificação não terminou em 2 minutos.'; exit 1 }
    if ($smoke.ExitCode) { Write-Error "$($smoke.ExitCode) página(s) falharam ao montar; veja %LOCALAPPDATA%\Vapt\logs."; exit 1 }
}

# 4) Instalador.
vpk pack `
    --packId Vapt.App `
    --packVersion $Version `
    --packTitle Vapt `
    --packAuthors 'blat0r' `
    --packDir $out `
    --mainExe Vapt.exe `
    --icon (Join-Path $root 'assets\Vapt.ico') `
    --shortcuts 'Desktop,StartMenuRoot' `
    --runtime $Runtime `
    --msi `
    --instLocation PerMachine `
    --outputDir $releases
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Get-ChildItem $releases -File | Where-Object { $_.Extension -in '.exe', '.msi' } | ForEach-Object {
    '{0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB)
}

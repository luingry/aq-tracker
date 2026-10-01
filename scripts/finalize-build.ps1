param(
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$latestLabel = Join-Path $repoRoot 'artifacts\AqTracker-latest.exe'

Write-Host 'Regra de implementação: gerando .exe final da build...' -ForegroundColor Cyan
& (Join-Path $repoRoot 'scripts\build-installer.ps1') -SkipTests:$SkipTests

if ($LASTEXITCODE -ne 0) {
    throw 'Build final falhou durante a geração do instalador.'
}

$artifact = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts') -Filter 'AqTracker-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $artifact) {
    throw 'Nenhum instalador encontrado em artifacts.'
}

Copy-Item -LiteralPath $artifact.FullName -Destination $latestLabel -Force
Write-Host "Arquivo consolidado para teste e distribuição: $latestLabel" -ForegroundColor Green
Write-Host "Última build: $($artifact.LastWriteTime) | $([math]::Round($artifact.Length / 1MB, 2)) MB" -ForegroundColor DarkGray

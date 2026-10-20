<#
    Builds the SwissTechKnife module package:
      artifacts/aq-tracker-<ver>.zip
        module.json, AqTracker.Module.dll         (loaded by the host)
        app/AqTracker.Hosted.exe (+ dlls, natives) (the AqTracker widget, run in its own process with --hosted)
    <ver> is VERSION (stamped into the packaged module.json). The SDK dll is never packaged: the host provides it.
    Prints sha256 and size for the host catalog.
    powershell -File scripts/pack-module.ps1
#>
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$moduleProject = Join-Path $root 'src/AqTracker.Module/AqTracker.Module.csproj'
$appProject = Join-Path $root 'src/AqTracker/AqTracker.csproj'
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$manifest = Get-Content -LiteralPath (Join-Path $root 'src/AqTracker.Module/module.json') -Raw | ConvertFrom-Json
$manifest.version = $version

$staging = Join-Path $root 'artifacts/module-staging'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
$moduleOut = Join-Path $staging 'module'
$appOut = Join-Path $staging 'app'

dotnet build $moduleProject -c $Configuration -o $moduleOut -v q -nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed: $moduleProject" }
dotnet publish $appProject -c $Configuration -o $appOut -v q -nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $appProject" }
if (-not (Test-Path -LiteralPath (Join-Path $appOut 'AqTracker.exe'))) { throw "AqTracker.exe not found in $appOut" }

$outDir = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zipPath = Join-Path $outDir "$($manifest.id)-$version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }

$skip = '.pdb', '.xml'
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    function Add-Entry([string]$file, [string]$entry) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $entry, 'Optimal') | Out-Null
    }
    $json = $manifest | ConvertTo-Json -Depth 5
    $entry = $zip.CreateEntry('module.json', 'Optimal')
    $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
    try { $writer.Write($json) } finally { $writer.Dispose() }

    Get-ChildItem -LiteralPath $moduleOut -File |
        Where-Object { $_.Name -ne 'module.json' -and $_.Name -notlike 'SwissTechKnife.Sdk.*' -and $_.Extension -notin $skip } |
        ForEach-Object { Add-Entry $_.FullName $_.Name }

    $appRoot = (Resolve-Path -LiteralPath $appOut).Path.TrimEnd('\') + '\'
    Get-ChildItem -LiteralPath $appOut -File -Recurse |
        Where-Object { $_.Extension -notin $skip -and $_.Name -notlike 'SwissTechKnife.Sdk.*' } |
        ForEach-Object {
            $relative = $_.FullName.Substring($appRoot.Length).Replace('\', '/')
            # The hosted copy gets its own image name (and matching .exe.config with the binding redirects),
            # so it is told apart from a standalone AqTracker.exe in Task Manager and by the standalone installer.
            if ($relative -eq 'AqTracker.exe') { $relative = 'AqTracker.Hosted.exe' }
            elseif ($relative -eq 'AqTracker.exe.config') { $relative = 'AqTracker.Hosted.exe.config' }
            Add-Entry $_.FullName "app/$relative"
        }
} finally { $zip.Dispose() }

Remove-Item -LiteralPath $staging -Recurse -Force

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $zipPath).Length
Write-Host "Packed $zipPath" -ForegroundColor Green
Write-Host "  version $version"
Write-Host "  sha256  $hash"
Write-Host "  size    $size"
[pscustomobject]@{ Id = $manifest.id; Version = $version; File = $zipPath; Sha256 = $hash; Size = $size }

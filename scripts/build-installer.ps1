[CmdletBinding()]
param(
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'installer\publish'
$installerScript = Join-Path $repoRoot 'installer\AqTracker.iss'
$versionPath = Join-Path $repoRoot 'VERSION'
$appVersion = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($appVersion) -or $appVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "VERSION must contain a simple SemVer value; found '$appVersion'."
}
if (-not $SkipTests) {
    & dotnet run --project (Join-Path $repoRoot 'tests\AqTracker.Tests\AqTracker.Tests.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed; installer was not created.' }
}

# .NET Framework 4.8 is supplied by supported Windows 10/11; publish only the app and managed dependencies.
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
& dotnet publish (Join-Path $repoRoot 'src\AqTracker\AqTracker.csproj') --configuration Release --output $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

function Find-InnoCompiler {
    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Source }

    $uninstallKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $install = Get-ItemProperty $uninstallKeys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } |
        Select-Object -First 1 -ExpandProperty InstallLocation
    if ($install) {
        $fromRegistry = Join-Path $install 'ISCC.exe'
        if (Test-Path -LiteralPath $fromRegistry) { return $fromRegistry }
    }

    foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

$iscc = Find-InnoCompiler

if ($null -eq $iscc) {
    Write-Host 'Installing Inno Setup with winget...'
    & winget install --id JRSoftware.InnoSetup --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup installation failed.' }
    $iscc = Find-InnoCompiler
    if ($null -eq $iscc) { throw 'Inno Setup was installed but ISCC.exe was not found.' }
}

& $iscc "/DAppVersion=$appVersion" "/O$(Join-Path $repoRoot 'artifacts')" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts') -Filter 'AqTracker-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 FullName, Length, LastWriteTime

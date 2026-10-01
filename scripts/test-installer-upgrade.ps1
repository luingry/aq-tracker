[CmdletBinding()]
param(
    [string] $BaselineInstaller,
    [string] $UpgradeInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$installedExe = Join-Path $env:LOCALAPPDATA 'Programs\Agent Quota Tracker\AqTracker.exe'
$settingsDir = Join-Path $env:APPDATA 'AqTracker'
$retentionMarker = Join-Path $settingsDir 'installer-qa-retention.marker'
$settingsDirExisted = Test-Path -LiteralPath $settingsDir

if (-not $UpgradeInstaller) {
    $UpgradeInstaller = Get-ChildItem -LiteralPath $artifacts -Filter 'AqTracker-Setup-*.exe' |
        Sort-Object { [version]($_.BaseName -replace '^AqTracker-Setup-', '') } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $BaselineInstaller) {
    $BaselineInstaller = Get-ChildItem -LiteralPath $artifacts -Filter 'AqTracker-Setup-*.exe' |
        Where-Object FullName -ne $UpgradeInstaller |
        Sort-Object { [version]($_.BaseName -replace '^AqTracker-Setup-', '') } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not (Test-Path -LiteralPath $BaselineInstaller) -or -not (Test-Path -LiteralPath $UpgradeInstaller)) {
    throw 'Baseline and upgrade installers are required.'
}

function Get-InstalledProcesses {
    @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $installedExe })
}

function Wait-ForProcessCount([int] $Expected, [int] $TimeoutSeconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $processes = @(Get-InstalledProcesses)
        if ($processes.Count -eq $Expected) { return $processes }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Expected $Expected installed process(es), found $($processes.Count): $($processes.ProcessId -join ',')."
}

function Invoke-Installer([string] $Path, [string] $LogName) {
    $logPath = Join-Path $env:TEMP $LogName
    $process = Start-Process -FilePath $Path -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$logPath" -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer failed with exit code $($process.ExitCode). Log: $logPath" }
    $logPath
}

if (@(Get-InstalledProcesses).Count -ne 0) {
    throw "QA requires no running process from the exact installed path: $installedExe"
}

New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
Set-Content -LiteralPath $retentionMarker -Value 'retain-on-uninstall' -NoNewline

$baselineLog = Invoke-Installer $BaselineInstaller 'aq-tracker-qa-baseline.log'
$first = Start-Process -FilePath $installedExe -PassThru
$second = Start-Process -FilePath $installedExe -PassThru
$baselineProcesses = Wait-ForProcessCount 2

$upgradeLog = Invoke-Installer $UpgradeInstaller 'aq-tracker-qa-upgrade.log'
[void](Wait-ForProcessCount 0)

$newFirst = Start-Process -FilePath $installedExe -PassThru
$upgradedProcesses = Wait-ForProcessCount 1
$newSecond = Start-Process -FilePath $installedExe -PassThru
$newSecond.WaitForExit(15000) | Out-Null
Start-Sleep -Milliseconds 300
if (@(Get-InstalledProcesses).Count -ne 1) { throw 'Single-instance mutex did not reject the second upgraded launch.' }

$uninstaller = Join-Path (Split-Path -Parent $installedExe) 'unins000.exe'
$uninstallLog = Join-Path $env:TEMP 'aq-tracker-qa-uninstall.log'
$uninstall = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstallLog" -Wait -PassThru
if ($uninstall.ExitCode -ne 0) { throw "Uninstaller failed with exit code $($uninstall.ExitCode)." }
[void](Wait-ForProcessCount 0)
if (Test-Path -LiteralPath $installedExe) { throw 'Installed executable remained after uninstall.' }
if (-not (Test-Path -LiteralPath $retentionMarker)) { throw 'Per-user settings directory was removed by uninstall.' }

Remove-Item -LiteralPath $retentionMarker -Force
if (-not $settingsDirExisted -and @(Get-ChildItem -LiteralPath $settingsDir -Force).Count -eq 0) {
    Remove-Item -LiteralPath $settingsDir
}

[pscustomobject]@{
    BaselineInstaller = $BaselineInstaller
    UpgradeInstaller = $UpgradeInstaller
    BaselinePids = $baselineProcesses.ProcessId -join ','
    UpgradedPid = $upgradedProcesses[0].ProcessId
    BaselineLog = $baselineLog
    UpgradeLog = $upgradeLog
    UninstallLog = $uninstallLog
    Result = 'PASS'
}

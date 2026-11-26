# Installs DeckWrench for this user: %LOCALAPPDATA%\Programs\DeckWrench, a Start menu entry, an entry in
# Settings > Apps (to uninstall), and (when Steam is closed) a Steam shortcut with DeckWrench's art.
# No admin rights needed. Re-run to repair; updates happen in the app.
#   install.ps1 [-Dest <folder>] [-NoSteam] [-RestartSteam] [-DesktopShortcut]
param(
    [string]$Dest = (Join-Path $env:LOCALAPPDATA 'Programs\DeckWrench'),
    [switch]$NoSteam,
    [switch]$RestartSteam,
    [switch]$DesktopShortcut
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$Dest = [IO.Path]::GetFullPath($Dest)

if ($src.TrimEnd('\') -ne $Dest.TrimEnd('\')) {
    if (Get-Process -Name DeckWrench -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dest, 'OrdinalIgnoreCase') }) {
        throw "DeckWrench is running from $Dest. Close it, then run the installer again."
    }
    $staging = "$Dest.installing"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $Dest) | Out-Null
    Copy-Item $src $staging -Recurse -Force
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    Move-Item $staging $Dest
}
# Files from a downloaded zip carry the "from the internet" mark; this copy is the one the user chose to install.
Get-ChildItem $Dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$exe = Join-Path $Dest 'DeckWrench.exe'
$cli = Join-Path $Dest 'deckwrench-cli.exe'
$version = (Get-Content (Join-Path $Dest 'README.txt') -TotalCount 1) -replace '^DeckWrench ([^ ]+).*$', '$1'

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $Dest
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Controller-first system toolbox for Proton, prefixes and storage'
    $lnk.Save()
}
$programs = [Environment]::GetFolderPath('Programs')
New-Shortcut (Join-Path $programs 'DeckWrench.lnk')
if ($DesktopShortcut) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeckWrench.lnk') }

# Settings > Apps entry, for this user only.
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeckWrench'
New-Item -Path $key -Force | Out-Null
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Dest 'uninstall.ps1')`""
@{
    DisplayName = 'DeckWrench'; DisplayVersion = $version; Publisher = 'DeckWrench'; DisplayIcon = "$exe,0"
    InstallLocation = $Dest; UninstallString = $uninstall; QuietUninstallString = "$uninstall -Quiet"
    URLInfoAbout = 'https://github.com/TheRoben-Dev/DeckWrench'
}.GetEnumerator() | ForEach-Object { New-ItemProperty -Path $key -Name $_.Key -Value $_.Value -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $key -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Write-Host "Installed DeckWrench $version to $Dest"
if (-not $NoSteam) {
    $steamArgs = @('steam-add')
    if ($RestartSteam) { $steamArgs += '--restart' }
    & $cli @steamArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Add DeckWrench to Steam later from DeckWrench > Settings > Updates, or run: `"$(Join-Path $Dest 'Install DeckWrench.cmd')`" -RestartSteam"
    }
}
exit 0

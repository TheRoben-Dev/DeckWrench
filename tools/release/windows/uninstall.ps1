# Removes DeckWrench's program files, Start menu entry, Settings > Apps entry and its Nexus link handler.
# Settings and prefix backups (%LOCALAPPDATA%\DeckWrench, %APPDATA%\DeckWrench) are kept unless -RemoveData.
param([switch]$Quiet, [switch]$RemoveData)
$ErrorActionPreference = 'Continue'
$dest = $PSScriptRoot
if (-not $Quiet) {
    $answer = Read-Host "Uninstall DeckWrench from $dest? Your settings and backups are kept. [y/N]"
    if ($answer -notmatch '^[yY]') { exit 1 }
}
Get-Process -Name DeckWrench -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dest, 'OrdinalIgnoreCase') } | Stop-Process -Force
foreach ($lnk in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'DeckWrench.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeckWrench.lnk'))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeckWrench' -Recurse -Force -ErrorAction SilentlyContinue
$nxm = (Get-ItemProperty 'HKCU:\Software\Classes\nxm\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
if ($nxm -and $nxm -like "*$dest*") { Remove-Item 'HKCU:\Software\Classes\nxm' -Recurse -Force }
if ($RemoveData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'DeckWrench'), (Join-Path $env:APPDATA 'DeckWrench') -Recurse -Force -ErrorAction SilentlyContinue
}
# This script runs from the folder being removed: finish from a separate process once it exits.
Start-Process cmd.exe -ArgumentList '/c', "timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`" & rmdir /s /q `"$dest.previous`"" -WindowStyle Hidden
Write-Host 'DeckWrench was uninstalled.'

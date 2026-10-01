# Daily backup of the target app's layout data (companion script shipped with DesktopGuard).
# All paths are derived from the current user, so the script is portable.
$ErrorActionPreference = 'Stop'

$src  = Join-Path $env:APPDATA '360DesktopLite\DTFence'
$reg  = 'HKCU:\Software\360desktoplite\DesktopWidgetPos'
$root = if ($env:DESKTOP_GUARD_BACKUP_DIR) { $env:DESKTOP_GUARD_BACKUP_DIR }
        else { Join-Path $env:LOCALAPPDATA 'DesktopGuard\external-backup' }

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$dst   = Join-Path $root "auto_$stamp"
New-Item -ItemType Directory -Force -Path $dst | Out-Null

$files = @('DTFenceData.dtf','DTFenceData.dtf.bk','Config.ini','reg.ini')
foreach ($f in $files) {
  $p = Join-Path $src $f
  if (Test-Path $p) { Copy-Item $p -Destination $dst -Force }
}
if (Test-Path (Join-Path $src 'Backup')) {
  Copy-Item (Join-Path $src 'Backup') -Destination $dst -Recurse -Force
}

# Keep the registry coordinates with the files, so a crash can be restored in one shot.
$rp = Get-ItemProperty $reg -ErrorAction SilentlyContinue
if ($rp) {
  $rp | Select-Object desktop_wigit_left,desktop_wigit_top,desktop_wigit_right,desktop_wigit_bottom,desktop_widget_pos,last_dpi |
    ConvertTo-Json | Set-Content (Join-Path $dst 'widgetpos.json') -Encoding UTF8
}

# Keep only the 30 most recent snapshots.
Get-ChildItem $root -Directory -Filter 'auto_*' | Sort-Object Name -Descending |
  Select-Object -Skip 30 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
# LYGO Browser - shortcut + protocol registration (per-user, reversible)
# Run:  pwsh -File tools\install_shortcuts.ps1
# Undo: pwsh -File tools\install_shortcuts.ps1 -Uninstall

param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'app\LygoBrowser.exe'
$bat  = Join-Path $root 'LYGO Browser.bat'
$ico  = Join-Path $root 'src\LygoBrowser\brand\lygo-browser.ico'
$desktop = [Environment]::GetFolderPath('Desktop')

if ($Uninstall) {
    Remove-Item 'HKCU:\Software\Classes\lygo' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $desktop 'LYGO Browser.lnk') -Force -ErrorAction SilentlyContinue
    Write-Host 'LYGO Browser protocol + shortcut removed.'
    exit 0
}

if (-not (Test-Path $exe)) { throw "missing exe: $exe  (run dotnet publish first)" }
if (-not (Test-Path $ico)) { throw "missing icon: $ico" }

# --- desktop shortcut (this is the 'picture skin' entry point) ---
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut((Join-Path $desktop 'LYGO Browser.lnk'))
$lnk.TargetPath       = $bat
$lnk.WorkingDirectory = $root
$lnk.IconLocation     = "$ico,0"
$lnk.Description      = 'LYGO Browser 1.0.0 - Bookmark Bible, agent + human'
$lnk.WindowStyle      = 7          # minimised console; the browser takes focus
$lnk.Save()

# --- lygo:// protocol so the console, forge, docs and agents can launch it ---
$base = 'HKCU:\Software\Classes\lygo'
New-Item -Path $base -Force | Out-Null
Set-ItemProperty -Path $base -Name '(default)'   -Value 'URL:LYGO Browser Protocol'
Set-ItemProperty -Path $base -Name 'URL Protocol' -Value ''
New-Item -Path "$base\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$base\DefaultIcon" -Name '(default)' -Value "$ico,0"
New-Item -Path "$base\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$base\shell\open\command" -Name '(default)' -Value "`"$exe`" `"%1`""

# --- Start menu entry ---
$startDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs'
$sl = $sh.CreateShortcut((Join-Path $startDir 'LYGO Browser.lnk'))
$sl.TargetPath = $bat; $sl.WorkingDirectory = $root; $sl.IconLocation = "$ico,0"
$sl.Description = 'LYGO Browser 1.0.0'; $sl.WindowStyle = 7; $sl.Save()

Write-Host 'OK  desktop shortcut : ' (Join-Path $desktop 'LYGO Browser.lnk')
Write-Host 'OK  start menu       : ' (Join-Path $startDir 'LYGO Browser.lnk')
Write-Host 'OK  protocol         : lygo://browser  ->  ' $exe

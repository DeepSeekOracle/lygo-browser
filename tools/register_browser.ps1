# Register the LYGO Browser as a Windows browser candidate.
# Windows 10/11 will NOT let a script become the default (UserChoice is hash-protected by design);
# this makes it appear in Settings -> Default apps and in "Open with", so Justin picks it once.
# Undo: pwsh -File tools\register_browser.ps1 -Uninstall

param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'app\LygoBrowser.exe'
$ico  = Join-Path $root 'src\LygoBrowser\brand\lygo-browser.ico'
$name = 'LYGO Browser'
$key  = 'LYGOBrowser'

if ($Uninstall) {
    foreach ($p in @("HKCU:\Software\Clients\StartMenuInternet\$key",
                     'HKCU:\Software\Classes\LYGOBrowser.Url',
                     'HKCU:\Software\Classes\LYGOBrowser.Html')) {
        Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $name -ErrorAction SilentlyContinue
    Write-Host 'LYGO Browser unregistered.'
    exit 0
}

if (-not (Test-Path $exe)) { throw "missing exe: $exe" }
$cmd = '"' + $exe + '" "%1"'

# --- app client entry (this is what "Default apps" enumerates) ---
$base = "HKCU:\Software\Clients\StartMenuInternet\$key"
New-Item "$base\shell\open\command" -Force | Out-Null
New-Item "$base\DefaultIcon" -Force | Out-Null
New-Item "$base\Capabilities\URLAssociations" -Force | Out-Null
New-Item "$base\Capabilities\FileAssociations" -Force | Out-Null
Set-ItemProperty $base -Name '(default)' -Value $name
Set-ItemProperty "$base\shell\open\command" -Name '(default)' -Value $cmd
Set-ItemProperty "$base\DefaultIcon" -Name '(default)' -Value "$ico,0"
Set-ItemProperty "$base\Capabilities" -Name 'ApplicationName' -Value $name
Set-ItemProperty "$base\Capabilities" -Name 'ApplicationDescription' -Value 'LYGO Browser - agent and human browser with the Bookmark Bible'
Set-ItemProperty "$base\Capabilities" -Name 'ApplicationIcon' -Value "$ico,0"
Set-ItemProperty "$base\Capabilities\URLAssociations" -Name 'http'  -Value 'LYGOBrowser.Url'
Set-ItemProperty "$base\Capabilities\URLAssociations" -Name 'https' -Value 'LYGOBrowser.Url'
Set-ItemProperty "$base\Capabilities\FileAssociations" -Name '.html' -Value 'LYGOBrowser.Html'
Set-ItemProperty "$base\Capabilities\FileAssociations" -Name '.htm'  -Value 'LYGOBrowser.Html'

# --- progids ---
foreach ($progid in 'LYGOBrowser.Url', 'LYGOBrowser.Html') {
    $pk = "HKCU:\Software\Classes\$progid"
    New-Item "$pk\shell\open\command" -Force | Out-Null
    New-Item "$pk\DefaultIcon" -Force | Out-Null
    Set-ItemProperty $pk -Name '(default)' -Value $name
    Set-ItemProperty "$pk\shell\open\command" -Name '(default)' -Value $cmd
    Set-ItemProperty "$pk\DefaultIcon" -Name '(default)' -Value "$ico,0"
}

# --- make Windows aware of the candidate ---
New-Item 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $name `
    -Value "Software\Clients\StartMenuInternet\$key\Capabilities"

Write-Host 'OK  registered as a browser candidate.'
Write-Host ''
Write-Host 'Windows will not let a script take the default (by design). One click is required:'
Write-Host '  1. press Win+R, type  ms-settings:defaultapps  and Enter'
Write-Host '  2. "Web browser" -> pick LYGO Browser'
Write-Host '  or right-click any .html file -> Open with -> Choose another app -> LYGO Browser -> Always'
Start-Process 'ms-settings:defaultapps'

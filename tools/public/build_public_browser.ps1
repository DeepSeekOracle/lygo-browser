# Build the PUBLIC browser package. Two phases, in this order, or the package leaks.
#   1. publish a Release build with -p:PublicBuild=true  (PathMap strips the builder's home
#      tree from the CodeView record; a plain copy of the dev app\ folder leaks it)
#   2. assemble a staging tree = dev tree minus app\, plus the published app\,
#      then hand it to sterilize.ps1, which scrubs and fails on any surviving leak
#
#   pwsh -File tools\public\build_public_browser.ps1
#   pwsh -File tools\public\build_public_browser.ps1 -Dest 'D:\somewhere\browser'
#
# The dev app\ folder is only read, never written: the browser is often running from it.

param(
    [string]$DevRoot = '<INSTALL_DIR>',
    [string]$Dest = '<VAULT>\PUBLIC_1.9.1\browser',
    [string]$WorkRoot = (Join-Path $env:TEMP 'lygo_public_build')
)

$ErrorActionPreference = 'Stop'
$csproj = Join-Path $DevRoot 'src\LygoBrowser\LygoBrowser.csproj'
$stage = Join-Path $WorkRoot 'stage'
$pubout = Join-Path $WorkRoot 'app'

foreach ($p in @($stage, $pubout)) {
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $p | Out-Null
}

# --- phase 1: clean binary ---------------------------------------------------
Write-Host "publishing $csproj (PublicBuild=true)"
& dotnet publish $csproj -c Release --nologo -o $pubout -p:PublicBuild=true -p:NoIncremental=true |
    Select-Object -Last 4
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$dll = Join-Path $pubout 'LygoBrowser.dll'
$exe = Join-Path $pubout 'LygoBrowser.exe'
foreach ($f in @($dll, $exe)) { if (-not (Test-Path -LiteralPath $f)) { throw "missing published artifact: $f" } }

$bad = @()
foreach ($f in @($dll, $exe)) {
    $s = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($f))
    if ($s -match 'C:[\\/]+Users[\\/]+[A-Za-z0-9_.\-]+') { $bad += (Split-Path $f -Leaf) }
}
if ($bad) { throw "published binary still contains a builder path: $($bad -join ', ') - check the PathMap property group" }

$ver = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
Write-Host ("  clean binary: {0}  file version {1}" -f (Split-Path $exe -Leaf), $ver.FileVersion)

# --- phase 2: staging tree ---------------------------------------------------
# The live app writes into data\ and can hold locks there, so the dev-tree copy is
# allowed to report failures (exit 8) - but it is NOT trusted: every file that should
# have arrived is asserted present afterwards.
Write-Host "staging $DevRoot -> $stage"
$skipDirs = @('app', 'src', 'data', 'jobs', 'save', 'obj', 'bin', 'dist', 'build',
              '__pycache__', '.vs', 'node_modules', '.git')
$robo = @($DevRoot, $stage, '/E', '/MT:16', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
foreach ($d in $skipDirs) { $robo += '/XD'; $robo += (Join-Path $DevRoot $d) }
& robocopy @robo | Out-Null
$roboCode = $LASTEXITCODE
if ($roboCode -ge 16) { throw "robocopy fatal ($roboCode)" }
if ($roboCode -band 8) { Write-Host "  note: robocopy reported unreadable file(s) (code $roboCode); verifying completeness" }

$expected = @()
foreach ($f in Get-ChildItem -LiteralPath $DevRoot -Recurse -File -Force -ErrorAction SilentlyContinue) {
    $rel = [IO.Path]::GetRelativePath($DevRoot, $f.FullName)
    $parts = $rel -split '\\'
    if ($parts | Where-Object { $skipDirs -contains $_ }) { continue }
    $expected += $rel
}
$missing = @()
foreach ($rel in $expected) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $rel))) { $missing += $rel }
}
if ($missing.Count) {
    throw "staging incomplete: $($missing.Count) of $($expected.Count) file(s) missing, e.g. $($missing[0..([Math]::Min(4, $missing.Count - 1))] -join ', ')"
}
Write-Host ("  staged {0} file(s), verified complete" -f $expected.Count)

# The shipped package carries the licence and the install notes at the root as well as
# under public\. Both are copies of one source of truth; the root pair is what a reader
# opening the zip sees first, so it is part of the layout, not an accident.
foreach ($n in 'LICENSE.md', 'INSTALL.md') {
    $from = Join-Path $stage "public\$n"
    if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $stage $n) -Force }
    else { Write-Host "  note: public\$n not found, root copy skipped" }
}

& robocopy $pubout (Join-Path $stage 'app') /E /MT:8 /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy of the published app failed ($LASTEXITCODE)" }

# --- phase 3: scrub + scan ---------------------------------------------------
& pwsh -NoLogo -NoProfile -File (Join-Path $DevRoot 'tools\public\sterilize.ps1') `
    -Source $stage -Dest $Dest -Kind browser
exit $LASTEXITCODE

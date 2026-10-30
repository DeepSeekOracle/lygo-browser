# Build a clean PUBLIC package from a DEV tree.
#   pwsh -File tools\public\sterilize.ps1 -Source <dev> -Dest <public> -Kind browser
# Scrub first, then scan: the build FAILS if any secret or developer path survives.
#
# 2026-10-30 gate fix (Delta/DSH). The old gate passed a package that still shipped
# `<USER>/...` and four `__pycache__\*.pyc` retaining `<USER>`:
#   1. scrub keys were backslash literals -> forward-slash paths survived the Replace
#   2. scan patterns were `<USER>`   -> forward-slash paths missed the regex
#   3. /XD received `Join-Path $Source '__pycache__'` -> only the TOP-LEVEL dir excluded,
#      so every nested __pycache__ shipped
#   4. the scan only opened $textExt files -> .pyc/.pdb/.bin were never read
# All four are structural, not one bad build. Do not "simplify" them back out.

param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Dest,
    [Parameter(Mandatory = $true)][ValidateSet('browser', 'console', 'forge', 'manager')][string]$Kind
)

$ErrorActionPreference = 'Stop'

if (([IO.Path]::GetFullPath($Source)).TrimEnd('\') -eq ([IO.Path]::GetFullPath($Dest)).TrimEnd('\')) {
    throw "Source and Dest are the same path: $Dest"
}
if ($Dest.Split('\').Count -lt 3) { throw "Dest is too close to a drive root: $Dest" }

# --- what never ships -------------------------------------------------------
# Path-based exclusions are deliberately TOP-LEVEL only, as before: `data`, `models`,
# `jobs`, `dist` are excluded at the root of a system tree, not at arbitrary depth.
$excludeDirPaths = @('data', 'src', 'tools\public', 'data\profile', 'data\vault', 'data\state',
                     'data\archive', 'data\screenshots', 'data\ingest', 'data\bible',
                     'jobs', 'save', 'models', 'workspace\logs', 'obj', 'bin', 'dist', 'build')
# Name-based exclusions apply at ANY depth. These four are the defect class that shipped.
$excludeDirNames = @('__pycache__', '.pytest_cache', '.git', '.vs', 'node_modules',
                     '.venv', '.venv-bark', '.venv-kokoro', '.mypy_cache', '.ruff_cache')
$excludeFiles = @('*.log', '*.bak', '*.bak-*', '*.tmp', '*.pdb', 'bridge.token', 'vault.dat',
                  'snaps.json', 'history.jsonl', 'downloads.jsonl', 'session.json', 'settings.json',
                  'api.json', '*.key', '*.pem', '.env', '*.sqlite', 'Thumbs.db', '.DS_Store')

$textExt = '.md', '.txt', '.json', '.jsonc', '.ps1', '.bat', '.cmd', '.py', '.js', '.mjs', '.ts',
            '.html', '.htm', '.css', '.cs', '.xaml', '.xaml.cs', '.yml', '.yaml', '.iss', '.spec',
            '.csproj', '.props', '.targets', '.xml', '.csv', '.toml', '.cfg', '.ini', '.sh', '.svg'

Write-Host "PUBLIC $Kind : $Source -> $Dest"

# --- scrub map: longest key first, so <INSTALL_DIR> wins over <USER> ----------
$scrubOrdered = [ordered]@{
    '<INSTALL_DIR>'             = '<INSTALL_DIR>'
    '<MANAGER_DIR>'      = '<MANAGER_DIR>'
    '<SIGNAL_DIR>'             = '<SIGNAL_DIR>'
    '<CONSOLE_DIR>' = '<CONSOLE_DIR>'
    '<STACK_DIR>'                  = '<STACK_DIR>'
    '<FORGE_DIR>'               = '<FORGE_DIR>'
    '<LYGO_DIR>'                     = '<LYGO_DIR>'
    '<DESKTOP>'                  = '<DESKTOP>'
    '<USER>'                          = '<USER>'
    '<VAULT>'           = '<VAULT>'
    '<EDRIVE>'                              = '<EDRIVE>'
    '<PROJECTS>'                        = '<PROJECTS>'
    '<ASSETS>'                          = '<ASSETS>'
    '<MEDIA>'                           = '<MEDIA>'
    '<VIDEO>'                           = '<VIDEO>'
    '<MODELS>'                          = '<MODELS>'
    '<VIDEO>'                           = '<VIDEO>'
}
# A path in the wild appears in at least four spellings: `<USER>`, `<USER>`,
# `<USER>` (JSON) and `<USER>` (JSON inside a Python string).
# So the pattern allows one-or-more of \ or / at every separator, and keys are applied
# longest-first so <INSTALL_DIR> wins over <USER>.
function Get-ScrubPattern {
    param([string]$Key)
    $parts = $Key -split '\\'
    return (($parts | ForEach-Object { [regex]::Escape($_) }) -join '[\\/]+')
}

$scrubPairs = @()
foreach ($k in $scrubOrdered.Keys) {
    $scrubPairs += [pscustomobject]@{ Key = $k; To = $scrubOrdered[$k]; Pattern = (Get-ScrubPattern $k) }
}
$scrubPairs = $scrubPairs | Sort-Object { $_.Key.Length } -Descending

# --- the scan: separator-agnostic, binary-safe ------------------------------
# Every path pattern accepts \ or / so a hand-edited or tool-generated path cannot slip.
$separ = '[\\/]+'
$patterns = @(
    ("C:{0}Users{0}justi" -f $separ),
    ("[A-Za-z]:{0}Users{0}justi" -f $separ),
    ("D:{0}LYGO" -f $separ),
    ("I:{0}E[ ]?Drive" -f $separ),
    ("E:{0}LYGO_BUILDER" -f $separ),
    'nvapi-[A-Za-z0-9_\-]{12,}',
    'sk-[A-Za-z0-9]{16,}',
    'sk-ant-[A-Za-z0-9\-_]{16,}',
    'AIza[0-9A-Za-z_\-]{30,}',
    '(ghp|gho|ghu|ghs)_[A-Za-z0-9]{20,}',
    'github_pat_[A-Za-z0-9_]{20,}',
    'hf_[A-Za-z0-9]{20,}',
    'xox[bpas]-[A-Za-z0-9\-]{10,}',
    'AKIA[0-9A-Z]{16}',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'Bearer [A-Za-z0-9\-\._]{20,}'
)

function Get-RelativeDisplay {
    param([string]$Root, [string]$Full)
    return [IO.Path]::GetRelativePath($Root, $Full)
}

function Test-TextLike {
    param([string]$Ext, [string[]]$Allowed)
    return ($Allowed -contains $Ext.ToLower())
}

# Scan every file. Text files report a line number; anything else is read in 1 MB
# chunks as Latin-1 so nothing in a .pyc, .dll, .bin or .gguf is invisible.
function Get-LeakHits {
    param([string]$Root, [string[]]$Patterns, [string[]]$Allowed)
    $hits = [System.Collections.Generic.List[string]]::new()
    $chunk = 1MB
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        $rel = Get-RelativeDisplay -Root $Root -Full $f.FullName
        if ((Test-TextLike -Ext $f.Extension -Allowed $Allowed) -and $f.Length -lt 16MB) {
            $n = 0
            foreach ($line in [IO.File]::ReadLines($f.FullName)) {
                $n++
                foreach ($p in $Patterns) {
                    if ($line -match $p) { $hits.Add("${rel}:${n}  [$p]"); break }
                }
            }
            continue
        }
        $fs = [IO.File]::OpenRead($f.FullName)
        try {
            $buf = $null
            $buf = [byte[]]::new($chunk)
            $carry = ''
            while (($read = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
                $s = $carry + [Text.Encoding]::Latin1.GetString($buf, 0, $read)
                $found = $null
                foreach ($p in $Patterns) {
                    $m = [regex]::Match($s, $p, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    if ($m.Success) { $found = $p; break }
                }
                if ($found) { $hits.Add("${rel}  [$found]  (binary scan)"); break }
                $carry = if ($s.Length -gt 4096) { $s.Substring($s.Length - 4096) } else { $s }
            }
        } finally { $fs.Dispose() }
    }
    return $hits
}

function Remove-ExcludedDirs {
    param([string]$Root, [string[]]$Names)
    $removed = 0
    $dirs = Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $Names -contains $_.Name } | Sort-Object { $_.FullName.Length } -Descending
    foreach ($d in $dirs) {
        if (Test-Path -LiteralPath $d.FullName) { Remove-Item -LiteralPath $d.FullName -Recurse -Force; $removed++ }
    }
    return $removed
}

function Remove-ExcludedFiles {
    param([string]$Root, [string[]]$Globs)
    $removed = 0
    foreach ($g in $Globs) {
        Get-ChildItem -LiteralPath $Root -Recurse -File -Force -Filter $g -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; $removed++ }
    }
    return $removed
}

function Invoke-Scrub {
    param([string]$Root, $Pairs, [string[]]$Allowed)
    $opts = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $changed = 0
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        if (-not (Test-TextLike -Ext $f.Extension -Allowed $Allowed)) { continue }
        $t = [IO.File]::ReadAllText($f.FullName)
        $o = $t
        foreach ($pair in $Pairs) {
            $t = [regex]::Replace($t, $pair.Pattern, $pair.To, $opts)
        }
        if ($t -ne $o) {
            [IO.File]::WriteAllText($f.FullName, $t, (New-Object System.Text.UTF8Encoding($false)))
            $changed++
        }
    }
    return $changed
}

# --- copy -------------------------------------------------------------------
if (Test-Path -LiteralPath $Dest) { Remove-Item -LiteralPath $Dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Dest | Out-Null

$robo = @($Source, $Dest, '/E', '/MT:16', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
foreach ($d in $excludeDirPaths) { $robo += '/XD'; $robo += (Join-Path $Source $d) }
foreach ($d in $excludeDirNames) { $robo += '/XD'; $robo += $d }
foreach ($f in $excludeFiles) { $robo += '/XF'; $robo += $f }
& robocopy @robo | Out-Null

# Robocopy's /XD name matching is not contractual; enforce it and prove it.
$nDirs = Remove-ExcludedDirs -Root $Dest -Names $excludeDirNames
$nFiles = Remove-ExcludedFiles -Root $Dest -Globs $excludeFiles
$leftover = Get-ChildItem -LiteralPath $Dest -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $excludeDirNames -contains $_.Name }
if ($leftover) { throw "excluded directories survived cleanup: $($leftover.FullName -join ', ')" }
Write-Host ("  removed {0} build dir(s), {1} file(s) at any depth" -f $nDirs, $nFiles)

$scrubbed = Invoke-Scrub -Root $Dest -Pairs $scrubPairs -Allowed $textExt
Write-Host "  scrubbed $scrubbed file(s)"

# --- fresh, empty, branded defaults ----------------------------------------
if ($Kind -eq 'browser') {
    $state = Join-Path $Dest 'data\state'
    New-Item -ItemType Directory -Force -Path $state | Out-Null
    @{ Homepage = 'https://excavationpro.ca/'; SearchEngine = 'google'; RestoreSession = $true
       DownloadDir = ''; Spellcheck = $true; AskBeforeDownload = $false } |
        ConvertTo-Json | Set-Content (Join-Path $state 'settings.json') -Encoding UTF8
}

# --- scan: anything left FAILS the build -----------------------------------
$hits = Get-LeakHits -Root $Dest -Patterns $patterns -Allowed $textExt

if ($hits.Count -gt 0) {
    Write-Host "PUBLIC BUILD FAILED - $($hits.Count) leak(s):" -ForegroundColor Red
    $hits | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

$m = Get-ChildItem -LiteralPath $Dest -Recurse -File -Force | Measure-Object Length -Sum
Write-Host ("PUBLIC {0} OK  {1:N0} files  {2:N1} MB  no secrets, no developer paths (text + binary)" -f
    $Kind, $m.Count, ($m.Sum/1MB))

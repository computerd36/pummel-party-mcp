<#
.SYNOPSIS
  Verified, copy-only backup of Pummel Party mod data. Never deletes or modifies the source.
#>
[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Rebuilt Games\Pummel Party'),
    [string]$DestinationRoot,
    [string]$GameRoot,
    [string[]]$Include = @('WorkshopMods', 'Rulesets', 'CustomModEditorLayouts', 'BoardSaves.pps',
                           'Unlocks.pps', 'OldUnlocksBackup.pps', 'LastUsedLayout.ini')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Defaults come from the untracked scripts\backup.settings.psd1 (see backup.settings.example.psd1).
# Command-line parameters win over the settings file.
$settingsPath = Join-Path $PSScriptRoot 'backup.settings.psd1'
$settings = @{}
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    $settings = Import-PowerShellDataFile -LiteralPath $settingsPath
}
if (-not $PSBoundParameters.ContainsKey('DestinationRoot') -and $settings.ContainsKey('DestinationRoot')) {
    $DestinationRoot = [string]$settings['DestinationRoot']
}
if (-not $PSBoundParameters.ContainsKey('GameRoot') -and $settings.ContainsKey('GameRoot')) {
    $GameRoot = [string]$settings['GameRoot']
}
if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    Write-Host "ERROR: No destination. Pass -DestinationRoot or create scripts\backup.settings.psd1 (copy scripts\backup.settings.example.psd1)." -ForegroundColor Red
    exit 1
}

$RequiredItems = @('WorkshopMods')
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-NormDir([string]$p) {
    return ([System.IO.Path]::GetFullPath($p)).TrimEnd('\', '/') + '\'
}

function Test-IsInside([string]$child, [string]$parent) {
    # True when $child equals or is located under $parent (case-insensitive, separator-terminated).
    $c = Get-NormDir $child
    $r = Get-NormDir $parent
    return $c.StartsWith($r, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Enumerates files and empty dirs for one include item (read-only on the source).
function Get-ItemEntries([string]$rootNorm, [string]$fullPath, $files, $emptyDirs) {
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $dirs = @($fullPath) + @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force -Directory | ForEach-Object { $_.FullName })
        foreach ($d in $dirs) {
            if (@(Get-ChildItem -LiteralPath $d -Force).Count -eq 0) {
                $emptyDirs.Add($d.Substring($rootNorm.Length)) | Out-Null
            }
        }
        foreach ($f in @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force -File)) {
            $files.Add($f) | Out-Null
        }
    } else {
        $files.Add((Get-Item -LiteralPath $fullPath -Force)) | Out-Null
    }
}

function Stop-Refuse([string]$msg) {
    Write-Host "REFUSED: $msg" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- safety checks
$repoRoot = Split-Path -Parent $PSScriptRoot
$destFull = [System.IO.Path]::GetFullPath($DestinationRoot)

if (Test-IsInside $destFull $SourceRoot) { Stop-Refuse "DestinationRoot '$destFull' is inside SourceRoot '$SourceRoot'." }
if (Test-IsInside $destFull $repoRoot)   { Stop-Refuse "DestinationRoot '$destFull' is inside the repo '$repoRoot'." }
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    Write-Warning "GameRoot unknown (no -GameRoot, no GameRoot in backup.settings.psd1): skipping the 'destination inside game folder' check."
} elseif (Test-IsInside $destFull $GameRoot) { Stop-Refuse "DestinationRoot '$destFull' is inside the game folder '$GameRoot'." }

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    Write-Host "ERROR: SourceRoot not found: $SourceRoot" -ForegroundColor Red
    exit 1
}

$rootNorm = Get-NormDir $SourceRoot
$included = New-Object System.Collections.Generic.List[string]
$skipped = New-Object System.Collections.Generic.List[string]

foreach ($item in $Include) {
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($rootNorm, $item))
    if (-not (Test-IsInside $full $rootNorm) -or ((Get-NormDir $full) -eq $rootNorm)) {
        Write-Host "ERROR: Include item '$item' is not strictly inside SourceRoot." -ForegroundColor Red
        exit 1
    }
    if (Test-Path -LiteralPath $full) {
        $included.Add($item) | Out-Null
    } elseif ($RequiredItems -contains $item) {
        Write-Host "ERROR: Required item '$item' is missing from $SourceRoot" -ForegroundColor Red
        exit 1
    } else {
        Write-Warning "Include item '$item' not found; skipping."
        $skipped.Add($item) | Out-Null
    }
}
foreach ($req in $RequiredItems) {
    if ($Include -notcontains $req) {
        Write-Host "ERROR: Required item '$req' is not in -Include." -ForegroundColor Red
        exit 1
    }
}

# ---------------------------------------------------------------- enumerate source
$srcFiles = New-Object System.Collections.Generic.List[object]
$emptyDirs = New-Object System.Collections.Generic.List[string]
foreach ($item in $included) {
    Get-ItemEntries $rootNorm ([System.IO.Path]::Combine($rootNorm, $item)) $srcFiles $emptyDirs
}

# ---------------------------------------------------------------- create backup folder
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = [System.IO.Path]::Combine($destFull, "backup-$stamp")
if (Test-Path -LiteralPath $backupDir) {
    Write-Host "ERROR: Backup folder already exists: $backupDir" -ForegroundColor Red
    exit 1
}
$dataDir = [System.IO.Path]::Combine($backupDir, 'data')
[System.IO.Directory]::CreateDirectory($dataDir) | Out-Null

function Stop-Failed([string]$msg) {
    Write-Host "BACKUP FAILED: $msg" -ForegroundColor Red
    try {
        [System.IO.File]::WriteAllText([System.IO.Path]::Combine($backupDir, 'FAILED.txt'),
            "Backup FAILED at $((Get-Date).ToString('o'))`r`n$msg`r`n", $utf8NoBom)
    } catch {
        Write-Host "Could not write FAILED.txt: $($_.Exception.Message)" -ForegroundColor Red
    }
    exit 1
}

try {
    # Hash source first.
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($f in $srcFiles) {
        $rel = $f.FullName.Substring($rootNorm.Length)
        $entries.Add([pscustomobject]@{
            Rel = $rel; Full = $f.FullName; Size = [int64]$f.Length
            Mtime = $f.LastWriteTimeUtc; Hash = (Get-Sha256 $f.FullName)
        }) | Out-Null
    }

    # Copy (copy only; never move/delete).
    foreach ($e in $entries) {
        $dst = [System.IO.Path]::Combine($dataDir, $e.Rel)
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($dst)) | Out-Null
        [System.IO.File]::Copy($e.Full, $dst, $false)
        $attrs = [System.IO.File]::GetAttributes($dst)
        if ($attrs -band [System.IO.FileAttributes]::ReadOnly) { [System.IO.File]::SetAttributes($dst, [System.IO.FileAttributes]::Normal) }
        [System.IO.File]::SetLastWriteTimeUtc($dst, $e.Mtime)
        if ($attrs -band [System.IO.FileAttributes]::ReadOnly) { [System.IO.File]::SetAttributes($dst, $attrs) }
    }
    foreach ($d in $emptyDirs) {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::Combine($dataDir, $d)) | Out-Null
    }

    # Verify copies.
    $dataNorm = Get-NormDir $dataDir
    $copied = @(Get-ChildItem -LiteralPath $dataDir -Recurse -Force -File)
    if ($copied.Count -ne $entries.Count) {
        Stop-Failed "File count mismatch: source $($entries.Count), copied $($copied.Count)."
    }
    $copiedMap = @{}
    foreach ($c in $copied) { $copiedMap[$c.FullName.Substring($dataNorm.Length).ToLowerInvariant()] = $c }
    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($e in $entries) {
        $k = $e.Rel.ToLowerInvariant()
        if (-not $copiedMap.ContainsKey($k)) { $bad.Add("missing copy: $($e.Rel)") | Out-Null; continue }
        $c = $copiedMap[$k]
        if ($c.Length -ne $e.Size -or (Get-Sha256 $c.FullName) -ne $e.Hash) { $bad.Add("hash/size mismatch: $($e.Rel)") | Out-Null }
    }
    foreach ($d in $emptyDirs) {
        if (-not (Test-Path -LiteralPath ([System.IO.Path]::Combine($dataDir, $d)) -PathType Container)) {
            $bad.Add("missing empty dir: $d") | Out-Null
        }
    }
    if ($bad.Count -gt 0) {
        Stop-Failed ("Verification failed ($($bad.Count) problems):`r`n" + (($bad | Select-Object -First 50) -join "`r`n"))
    }

    # Manifest.
    $totalBytes = [int64]0
    foreach ($e in $entries) { $totalBytes += $e.Size }
    $fileList = @($entries | ForEach-Object {
        [ordered]@{ path = $_.Rel; size = $_.Size; sha256 = $_.Hash; lastWriteTimeUtc = $_.Mtime.ToString('o') }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        createdUtc    = (Get-Date).ToUniversalTime().ToString('o')
        computerName  = $env:COMPUTERNAME
        sourceRoot    = $SourceRoot
        includedItems = @($included)
        skippedItems  = @($skipped)
        fileCount     = $entries.Count
        totalBytes    = $totalBytes
        files         = $fileList
        emptyDirs     = @($emptyDirs)
    }
    $json = $manifest | ConvertTo-Json -Depth 6
    $manifestPath = [System.IO.Path]::Combine($backupDir, 'manifest.json')
    [System.IO.File]::WriteAllText($manifestPath, $json, $utf8NoBom)
    $mh = Get-Sha256 $manifestPath
    [System.IO.File]::WriteAllText($manifestPath + '.sha256', $mh + "`r`n", $utf8NoBom)

    # Protect backup files.
    foreach ($f in @(Get-ChildItem -LiteralPath $backupDir -Recurse -Force -File)) { $f.IsReadOnly = $true }
} catch {
    Stop-Failed "Unexpected error: $($_.Exception.Message)"
}

$sw.Stop()
Write-Host "Backup OK: $backupDir"
Write-Host ("Files: {0}  Size: {1:N2} MB  Skipped: {2}  Duration: {3:N1}s" -f $entries.Count, ($totalBytes / 1MB), $skipped.Count, $sw.Elapsed.TotalSeconds)
exit 0

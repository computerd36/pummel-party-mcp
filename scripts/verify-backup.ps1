<#
.SYNOPSIS
  Strictly read-only verification of PummelBridge backups. Writes nothing.
#>
[CmdletBinding()]
param(
    [string[]]$BackupRoot = @('D:\PummelBackups'),
    [switch]$All,
    [string]$BackupPath,
    [switch]$CompareSource,
    [string]$SourceRoot = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Rebuilt Games\Pummel Party')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormDir([string]$p) {
    return ([System.IO.Path]::GetFullPath($p)).TrimEnd('\', '/') + '\'
}
function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-Backup([string]$dir) {
    $problems = New-Object System.Collections.Generic.List[string]
    $count = 0
    try {
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
            $problems.Add("backup folder not found") | Out-Null
            return [pscustomobject]@{ Path = $dir; Problems = $problems; Count = 0; Manifest = $null }
        }
        $manifestPath = [System.IO.Path]::Combine($dir, 'manifest.json')
        $shaPath = $manifestPath + '.sha256'
        $dataDir = [System.IO.Path]::Combine($dir, 'data')
        $manifest = $null

        if (Test-Path -LiteralPath ([System.IO.Path]::Combine($dir, 'FAILED.txt'))) { $problems.Add("FAILED.txt present (backup was marked failed)") | Out-Null }

        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            $problems.Add("manifest.json missing") | Out-Null
        } elseif (-not (Test-Path -LiteralPath $shaPath -PathType Leaf)) {
            $problems.Add("manifest.json.sha256 missing") | Out-Null
        } else {
            $expected = ([System.IO.File]::ReadAllText($shaPath).Trim() -split '\s+')[0].ToLowerInvariant()
            if ((Get-Sha256 $manifestPath) -ne $expected) {
                $problems.Add("manifest.json hash does not match manifest.json.sha256") | Out-Null
            } else {
                try { $manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json }
                catch { $problems.Add("manifest.json unparseable: $($_.Exception.Message)") | Out-Null }
            }
        }

        if ($null -ne $manifest) {
            if (-not (Test-Path -LiteralPath $dataDir -PathType Container)) {
                $problems.Add("data folder missing") | Out-Null
            } else {
                $dataNorm = Get-NormDir $dataDir
                $files = @($manifest.files)
                $count = $files.Count
                if ([int]$manifest.fileCount -ne $files.Count) {
                    $problems.Add("manifest fileCount $($manifest.fileCount) != files listed $($files.Count)") | Out-Null
                }
                $known = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                foreach ($f in $files) {
                    $rel = [string]$f.path
                    $known.Add($rel) | Out-Null
                    $full = [System.IO.Path]::Combine($dataDir, $rel)
                    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { $problems.Add("missing: $rel") | Out-Null; continue }
                    $len = (Get-Item -LiteralPath $full -Force).Length
                    if ($len -ne [int64]$f.size) { $problems.Add("size mismatch: $rel (expected $($f.size), got $len)") | Out-Null; continue }
                    if ((Get-Sha256 $full) -ne ([string]$f.sha256).ToLowerInvariant()) { $problems.Add("hash mismatch: $rel") | Out-Null }
                }
                $actual = @(Get-ChildItem -LiteralPath $dataDir -Recurse -Force -File)
                foreach ($a in $actual) {
                    $rel = $a.FullName.Substring($dataNorm.Length)
                    if (-not $known.Contains($rel)) { $problems.Add("extra file not in manifest: $rel") | Out-Null }
                }
                if ($actual.Count -ne $files.Count) {
                    $problems.Add("file count on disk $($actual.Count) != manifest $($files.Count)") | Out-Null
                }
                foreach ($d in @($manifest.emptyDirs)) {
                    if ($d -and -not (Test-Path -LiteralPath ([System.IO.Path]::Combine($dataDir, [string]$d)) -PathType Container)) {
                        $problems.Add("missing empty dir: $d") | Out-Null
                    }
                }
            }
        }
    } catch {
        $problems.Add("unexpected error: $($_.Exception.Message)") | Out-Null
    }
    return [pscustomobject]@{ Path = $dir; Problems = $problems; Count = $count; Manifest = $manifest }
}

# ---------------------------------------------------------------- select backups
$targets = New-Object System.Collections.Generic.List[string]
$rootErrors = 0

if ($BackupPath) {
    $targets.Add($BackupPath) | Out-Null
} else {
    foreach ($root in $BackupRoot) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            Write-Host "FAIL: backup root not found: $root" -ForegroundColor Red
            $rootErrors++
            continue
        }
        $dirs = @(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object { $_.Name -like 'backup-*' } | Sort-Object Name)
        if ($dirs.Count -eq 0) {
            Write-Host "FAIL: no backup-* folders in root: $root" -ForegroundColor Red
            $rootErrors++
            continue
        }
        if ($All) { foreach ($d in $dirs) { $targets.Add($d.FullName) | Out-Null } }
        else { $targets.Add($dirs[$dirs.Count - 1].FullName) | Out-Null }
    }
}

$failed = $rootErrors
$results = @()
foreach ($t in $targets) {
    $r = Test-Backup $t
    $results += $r
    if ($r.Problems.Count -eq 0) {
        Write-Host "PASS  $t  ($($r.Count) files verified)" -ForegroundColor Green
    } else {
        $failed++
        Write-Host "FAIL  $t  ($($r.Problems.Count) problems)" -ForegroundColor Red
        foreach ($p in ($r.Problems | Select-Object -First 20)) { Write-Host "   - $p" }
        if ($r.Problems.Count -gt 20) { Write-Host "   ... and $($r.Problems.Count - 20) more" }
    }
}

# ---------------------------------------------------------------- informational drift report
if ($CompareSource) {
    try {
        $newest = @($results | Where-Object { $null -ne $_.Manifest } | Sort-Object { Split-Path -Leaf $_.Path }) | Select-Object -Last 1
        if ($null -eq $newest) {
            Write-Host "INFO: CompareSource skipped (no readable backup manifest)."
        } elseif (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
            Write-Host "INFO: CompareSource skipped (source not found: $SourceRoot)."
        } else {
            $rootNorm = Get-NormDir $SourceRoot
            $live = @{}
            foreach ($item in @($newest.Manifest.includedItems)) {
                $p = [System.IO.Path]::Combine($rootNorm, [string]$item)
                if (-not (Test-Path -LiteralPath $p)) { continue }
                if (Test-Path -LiteralPath $p -PathType Container) {
                    foreach ($f in @(Get-ChildItem -LiteralPath $p -Recurse -Force -File)) { $live[$f.FullName.Substring($rootNorm.Length).ToLowerInvariant()] = $f }
                } else {
                    $f = Get-Item -LiteralPath $p -Force
                    $live[$f.FullName.Substring($rootNorm.Length).ToLowerInvariant()] = $f
                }
            }
            $added = @(); $removed = @(); $changed = @()
            $inBackup = @{}
            foreach ($f in @($newest.Manifest.files)) {
                $rel = [string]$f.path
                $k = $rel.ToLowerInvariant()
                $inBackup[$k] = $true
                if (-not $live.ContainsKey($k)) { $removed += $rel; continue }
                $lf = $live[$k]
                if ($lf.Length -ne [int64]$f.size -or (Get-Sha256 $lf.FullName) -ne ([string]$f.sha256).ToLowerInvariant()) { $changed += $rel }
            }
            foreach ($k in $live.Keys) {
                if (-not $inBackup.ContainsKey($k)) { $added += $live[$k].FullName.Substring($rootNorm.Length) }
            }
            Write-Host "INFO: live source vs newest backup ($($newest.Path)):"
            foreach ($pair in @(@('added', $added), @('removed', $removed), @('changed', $changed))) {
                $list = @($pair[1])
                Write-Host ("  {0}: {1}" -f $pair[0], $list.Count)
                foreach ($x in ($list | Select-Object -First 20)) { Write-Host "     $x" }
            }
            if ($added.Count + $removed.Count + $changed.Count -eq 0) { Write-Host "  Backup is up to date." }
            else { Write-Host "  Backup is STALE; run backup-mods.ps1 again." }
        }
    } catch {
        Write-Host "INFO: CompareSource failed: $($_.Exception.Message)"
    }
}

if ($failed -gt 0 -or $targets.Count -eq 0) {
    Write-Host "RESULT: FAIL" -ForegroundColor Red
    exit 1
}
Write-Host "RESULT: PASS" -ForegroundColor Green
exit 0

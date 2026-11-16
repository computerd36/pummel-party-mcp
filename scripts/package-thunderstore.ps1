<#
.SYNOPSIS
  Builds the plugin (Release) and assembles a Thunderstore package. PREPARES ONLY: nothing is uploaded or published.

.DESCRIPTION
  Output (gitignored): artifacts\thunderstore\  and  artifacts\PummelPartyMcp-<version>.zip
  Package content: manifest.json, README.md, CHANGELOG.md (copied from the root CHANGELOG.md), icon.png, BepInEx\plugins\PummelPartyMcp\PummelPartyMcp.Plugin.dll
  The version comes from <Version> in the plugin csproj, so it cannot drift.
  Only the plugin's own DLL is packaged; the script fails if any other DLL would be included.
  The game folder is only read by the build (reference assemblies); nothing is written there.
  Windows PowerShell 5.1 compatible.

.PARAMETER GameDir
  Game install folder, passed to the build as -p:GameDir. When omitted the csproj resolves it itself
  (src\GameDir.props.user, else the standard Steam location; see src\Directory.Build.props).

.PARAMETER AllowDirty
  Package even if there are uncommitted changes under src\PummelPartyMcp.Plugin (default: refuse).

.EXAMPLE
  .\scripts\package-thunderstore.ps1
  .\scripts\package-thunderstore.ps1 -GameDir "D:\Games\Pummel Party" -AllowDirty
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pluginDir = Join-Path $repo 'src\PummelPartyMcp.Plugin'
$csproj = Join-Path $pluginDir 'PummelPartyMcp.Plugin.csproj'
$template = Join-Path $repo 'packaging\thunderstore'
$artifacts = Join-Path $repo 'artifacts'
$stage = Join-Path $artifacts 'thunderstore'
$pluginName = 'PummelPartyMcp'
$dllName = 'PummelPartyMcp.Plugin.dll'

# --- version from the csproj
[xml]$proj = Get-Content -LiteralPath $csproj -Raw
$version = $null
foreach ($pg in @($proj.Project.PropertyGroup)) {
    if ($pg.PSObject.Properties['Version'] -and $pg.Version) { $version = ([string]$pg.Version).Trim() }
}
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+$') { throw "Could not read a MAJOR.MINOR.PATCH <Version> from $csproj (got '$version')." }

# --- refuse a dirty plugin tree
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    $dirty = & git -C $repo status --porcelain -- 'src/PummelPartyMcp.Plugin'
    if ($dirty -and -not $AllowDirty) {
        throw "Uncommitted changes under src\PummelPartyMcp.Plugin (pass -AllowDirty to package anyway):`n$($dirty -join "`n")"
    }
    if ($dirty) { Write-Warning 'Packaging with uncommitted changes under src\PummelPartyMcp.Plugin (-AllowDirty).' }
}
elseif (-not $AllowDirty) {
    throw 'git was not found, so the working tree cannot be checked; pass -AllowDirty to continue.'
}

# --- build
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'dotnet (the .NET SDK) was not found on PATH.' }
$buildArgs = @('build', $csproj, '-c', 'Release', '-nologo', '-v', 'q', "-p:PathMap=$repo\=/_/")
if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
Write-Host "Building $dllName $version ..."
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }
$builtDll = Join-Path $pluginDir "bin\Release\$dllName"
if (-not (Test-Path -LiteralPath $builtDll)) { throw "Build output not found: $builtDll" }

# --- sanity: no local paths embedded in the DLL
$bytes = [System.IO.File]::ReadAllBytes($builtDll)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
foreach ($needle in @($repo, ($env:USERPROFILE))) {
    if ($needle -and ($ascii.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $utf16.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
        throw "The built DLL contains the local path '$needle'; refusing to package it."
    }
}

# --- assemble (only below the repo's artifacts folder)
if (-not $stage.StartsWith($artifacts + '\')) { throw 'Internal error: staging folder outside artifacts.' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$pluginOut = Join-Path $stage "BepInEx\plugins\$pluginName"
New-Item -ItemType Directory -Force -Path $pluginOut | Out-Null
Copy-Item -LiteralPath $builtDll -Destination (Join-Path $pluginOut $dllName)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$readme = (Get-Content -LiteralPath (Join-Path $template 'README.md') -Raw) -replace '\{\{VERSION\}\}', $version
[System.IO.File]::WriteAllText((Join-Path $stage 'README.md'), $readme, $utf8NoBom)
# The root CHANGELOG.md is the source of truth; packaging\thunderstore\CHANGELOG.md is a copy kept identical.
$rootChangelog = Join-Path $repo 'CHANGELOG.md'
if (-not (Test-Path -LiteralPath $rootChangelog -PathType Leaf)) { throw "Root CHANGELOG.md not found: $rootChangelog" }
Copy-Item -LiteralPath $rootChangelog -Destination (Join-Path $stage 'CHANGELOG.md')
Copy-Item -LiteralPath (Join-Path $template 'icon.png') -Destination (Join-Path $stage 'icon.png')

$description = "Lets an AI agent see and edit Pummel Party's Mod Editor through MCP. Sandbox mods only, loopback only; blocks Workshop publishing while enabled. Unofficial."
if ($description.Length -gt 250) { throw "Description is $($description.Length) chars; Thunderstore allows 250." }
$manifest = [ordered]@{
    name           = $pluginName
    version_number = $version
    website_url    = 'https://github.com/computerd36/pummel-party-mcp'
    description    = $description
    dependencies   = @()  # PLACEHOLDER: add the BepInEx pack dependency string of this game's Thunderstore community (not known from the repo; do not guess)
}
[System.IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8NoBom)

# --- guard: exactly the plugin DLL, nothing else
$dlls = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.dll')
if ($dlls.Count -ne 1 -or $dlls[0].Name -ne $dllName) {
    throw "Package must contain exactly $dllName and no other DLL; found: $(($dlls | ForEach-Object { $_.FullName }) -join ', ')"
}
$bad = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.exe', '.token' -or $_.Name -like '*.cfg' })
if ($bad.Count -gt 0) { throw "Unexpected files in package: $(($bad | ForEach-Object { $_.Name }) -join ', ')" }

# --- zip (forward-slash entry names; Compress-Archive in 5.1 writes backslashes)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $artifacts "$pluginName-$version.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in (Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)) {
        $entry = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

# --- report
Write-Host ''
Write-Host "Package: $zipPath"
$zr = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try { foreach ($e in $zr.Entries) { '{0,10:N0}  {1}' -f $e.Length, $e.FullName } } finally { $zr.Dispose() }
Write-Host ('Zip size: {0:N0} bytes' -f (Get-Item -LiteralPath $zipPath).Length)
Write-Host 'Prepared only. Nothing was uploaded or published.'

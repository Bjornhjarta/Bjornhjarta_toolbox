# Validates the integrity and basic structure of the RC2 Windows portable ZIP.
# PowerShell 5.1+; no game installation or application execution required.
param(
    [string]$PackagePath = '',
    [string]$ChecksumsFile = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $root 'artifacts\Bjornhjarta-v1.0.0-rc2-win-x64-portable.zip'
}
if ([string]::IsNullOrWhiteSpace($ChecksumsFile)) {
    $ChecksumsFile = Join-Path $root 'artifacts\SHA256SUMS.txt'
}
$PackagePath = [System.IO.Path]::GetFullPath($PackagePath)
$ChecksumsFile = [System.IO.Path]::GetFullPath($ChecksumsFile)
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "Package not found: $PackagePath" }
if (-not (Test-Path -LiteralPath $ChecksumsFile -PathType Leaf)) { throw "Checksum file not found: $ChecksumsFile" }

$matching = @()
foreach ($line in (Get-Content -LiteralPath $ChecksumsFile)) {
    if ($line -match '^([a-fA-F0-9]{64})\s+(.+)$') {
        if ($Matches[2] -eq [System.IO.Path]::GetFileName($PackagePath)) {
            $matching += $Matches[1].ToLowerInvariant()
        }
    }
}
if ($matching.Count -ne 1) { throw 'Expected exactly one checksum entry for the portable package.' }
$actual = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $matching[0]) { throw "SHA-256 mismatch: $PackagePath" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entries = @($archive.Entries)
    if ($entries.Count -lt 5) { throw "Portable ZIP has too few entries: $($entries.Count)" }
    $paths = @($entries | ForEach-Object { $_.FullName })
    foreach ($required in @('Bjornhjarta.Desktop.exe', 'Bjornhjarta.Desktop.dll', 'Bjornhjarta.Core.dll', 'Bjornhjarta.Desktop.deps.json', 'Bjornhjarta.Desktop.runtimeconfig.json', 'README-FIRST.txt')) {
        if ($paths -cnotcontains $required) { throw "Portable ZIP is missing: $required" }
    }
    # Windows treats ZIP names case-insensitively; reject duplicate or case-colliding paths.
    $uniquePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if (-not $uniquePaths.Add($entry.FullName)) {
            throw "Portable ZIP has a duplicate or case-colliding entry: $($entry.FullName)"
        }
        $parts = $entry.FullName.Replace('\', '/').Split('/')
        if ($entry.FullName.StartsWith('/') -or $entry.FullName.Contains(':') -or
            ($parts -contains '..') -or ($parts -contains '.')) {
            throw "Portable ZIP contains unsafe entry path: $($entry.FullName)"
        }
    }
}
finally { $archive.Dispose() }
Write-Host "PASS: Portable RC2 ZIP has required files and a matching SHA-256: $PackagePath" -ForegroundColor Green

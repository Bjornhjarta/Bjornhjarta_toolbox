# Creates a portable, self-contained Windows x64 RELEASE CANDIDATE package, not a stable release.
# PowerShell 5.1+ and the .NET 10 SDK are required.
# Hotfix 1: Publish to a quote-free TEMP path; keep archives in the requested artifacts folder.
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION.txt') -Raw).Trim()
if ($version -ne '1.0.0-rc.2') { throw "Unexpected version marker: $version" }

# Always validate first (including on CI). Validation itself fails hard on build/test errors.
& (Join-Path $PSScriptRoot 'validate-release.ps1')
if (-not $?) { throw 'Release validation script failed' }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# IMPORTANT: Microsoft's publish targets use an MSBuild item transform resembling
# @(Files->'$(PublishDir)%(RelativePath)'). An apostrophe in PublishDir can disrupt
# this expression and result in MSB3094 (401 source files, 1 destination).
# Project/output artifact paths can still contain apostrophes; only the MSBuild
# publish staging directory must be free of apostrophes and semicolons.
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
if ($tempRoot.Contains("'") -or $tempRoot.Contains(';')) {
    throw "The Windows TEMP path contains an apostrophe or semicolon and cannot be used safely for dotnet publish: $tempRoot. Choose a different TEMP/TMP directory and rerun."
}
$publish = Join-Path $tempRoot ('Bjornhjarta-publish-' + [Guid]::NewGuid().ToString('N'))

$archivePath = Join-Path $OutputDirectory 'Bjornhjarta-v1.0.0-rc2-win-x64-portable.zip'
$archiveTemp = Join-Path $OutputDirectory ('Bjornhjarta-package-' + [Guid]::NewGuid().ToString('N') + '.tmp.zip')
try {
    New-Item -ItemType Directory -Path $publish -ErrorAction Stop | Out-Null
    Write-Host "Publishing to a safe temporary directory: $publish"

    & dotnet publish .\src\Bjornhjarta.Desktop\Bjornhjarta.Desktop.csproj `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
        -p:PublishTrimmed=false -p:Version=1.0.0-rc.2 -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Portable Windows publish failed' }

    $exe = Join-Path $publish 'Bjornhjarta.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Published application missing: $exe"
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs\PORTABLE-RELEASE-README.txt') `
        -Destination (Join-Path $publish 'README-FIRST.txt') -ErrorAction Stop

    # Zip creation uses the .NET file API, so apostrophes in artifacts paths are safe.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publish, $archiveTemp, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    if (-not (Test-Path -LiteralPath $archiveTemp -PathType Leaf) -or
        (Get-Item -LiteralPath $archiveTemp).Length -eq 0) {
        throw "Preview archive was not created: $archiveTemp"
    }

    # Replace the old ZIP only after a complete new ZIP was produced.
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction Stop
    }
    Move-Item -LiteralPath $archiveTemp -Destination $archivePath -ErrorAction Stop
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = @{
        version = $version
        package = [System.IO.Path]::GetFileName($archivePath)
        sha256 = $checksum
        runtimeIdentifier = 'win-x64'
        configuration = 'Release'
        selfContained = $true
        coreTestsPassedDuringPackaging = $true
        manualWpfAcceptanceCompleted = $false
        stableRelease = $false
        publishPathHotfix = $true
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package-manifest.json') -Encoding UTF8
    "$checksum  $([System.IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $archivePath -ChecksumsFile (Join-Path $OutputDirectory 'SHA256SUMS.txt')
    if (-not $?) { throw 'Published package verification failed' }
    Write-Host "PASS: Published RC2 portable package: $archivePath" -ForegroundColor Green
    Write-Host "SHA256: $checksum" -ForegroundColor Green
    Write-Warning 'This is v1.0.0-rc.2, NOT v1.0.0 stable. Complete interactive WPF acceptance tests.'
}
finally {
    if (Test-Path -LiteralPath $publish) {
        Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $archiveTemp) {
        Remove-Item -LiteralPath $archiveTemp -Force -ErrorAction SilentlyContinue
    }
}

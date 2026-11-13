# Run from the project root with Windows PowerShell 5.1+ and .NET 10 SDK.
# Never publish or mark stable when a build or smoke test fails.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION.txt') -Raw).Trim()
if ($version -ne '1.0.0-rc.2') { throw "Unexpected version marker: $version" }
Write-Host "Bjornhjarta $version validation: $root"

& dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'The .NET SDK could not be started.' }
& dotnet restore .\Bjornhjarta.sln
if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed' }
& dotnet restore .\tests\Bjornhjarta.Core.SmokeTests\Bjornhjarta.Core.SmokeTests.csproj
if ($LASTEXITCODE -ne 0) { throw 'Smoke-test restore failed' }
& dotnet clean .\Bjornhjarta.sln --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Clean failed' }
& dotnet build .\Bjornhjarta.sln --configuration Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
& dotnet run --project .\tests\Bjornhjarta.Core.SmokeTests\Bjornhjarta.Core.SmokeTests.csproj --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed' }
Write-Host 'PASS: RC2 Release build and Core smoke tests. Interactive WPF acceptance STILL required.' -ForegroundColor Green

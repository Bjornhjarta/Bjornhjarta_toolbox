# Bjornhjarta 1.0.0-rc.2: smoke-test the EXTRACTED win-x64 portable EXE.
# Requires an interactive, unlocked Windows desktop. Never modifies game/server files.
# PowerShell 5.1+; run AFTER package-release.ps1 has succeeded.
param(
    [string]$PackagePath = '',
    [int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $root 'artifacts\Bjornhjarta-v1.0.0-rc2-win-x64-portable.zip'
}
$PackagePath = [System.IO.Path]::GetFullPath($PackagePath)
$sumFile = Join-Path (Split-Path -Parent $PackagePath) 'SHA256SUMS.txt'
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $PackagePath -ChecksumsFile $sumFile
if (-not $?) { throw 'ZIP integrity verification failed' }

$reportPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Bjornhjarta\Logs\ui-smoke-rc2.json'
$logPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Bjornhjarta\Logs\bjornhjarta.log'

# RC2 hotfix: On failure, show the actual WPF exception and failed check without
# requiring the user to locate or manually open AppData diagnostics files.
function Show-UiSmokeDiagnostics {
    Write-Host ''
    Write-Host '=== BJORN HJARTA WPF UI SMOKE DIAGNOSTICS ===' -ForegroundColor Yellow
    if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
        try {
            $report = Get-Content -LiteralPath $reportPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
            Write-Host "Report: $reportPath"
            Write-Host "Version: $($report.version) / Passed: $($report.passed)"
            foreach ($item in @($report.checks)) {
                $status = if ($item.Passed -eq $true) { 'PASS' } else { 'FAIL' }
                Write-Host "$status`: $($item.Name)"
                if ($item.Passed -ne $true -and $item.Detail) {
                    $message = [string]$item.Detail
                    if ($message.Length -gt 6000) { $message = $message.Substring(0, 6000) + ' ... [truncated]' }
                    Write-Host $message
                }
            }
        }
        catch { Write-Warning "Could not read UI smoke report: $_" }
    }
    else { Write-Warning "No WPF smoke report was created: $reportPath" }

    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        Write-Host "Last 55 diagnostic log lines: $logPath"
        Get-Content -LiteralPath $logPath -Tail 55 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    }
    else { Write-Warning "No desktop diagnostic log exists: $logPath" }
    Write-Host '=== END UI SMOKE DIAGNOSTICS ==='
    Write-Host ''
}
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath -Force }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('Bjornhjarta-ui-smoke-' + [Guid]::NewGuid().ToString('N'))
$proc = $null
try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $stage -Force
    $exe = Join-Path $stage 'Bjornhjarta.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published EXE is missing after extraction.' }
    Write-Host "Launching EXTRACTED RC2 EXE for read-only UI smoke test: $exe"
    $proc = Start-Process -FilePath $exe -ArgumentList '--ui-smoke' -PassThru
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill() } catch {}
        Show-UiSmokeDiagnostics
        throw "UI smoke test timed out after $TimeoutSeconds seconds. Check the diagnostics above."
    }
    if ($proc.ExitCode -ne 0) {
        Show-UiSmokeDiagnostics
        throw "UI smoke EXE exited with code $($proc.ExitCode). See WPF exception details above."
    }
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "UI smoke test produced no report: $reportPath" }
    $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($result.version -ne '1.0.0-rc.2' -or $result.passed -ne $true -or @($result.checks).Count -ne 5) {
        throw "UI smoke report failed version/checks validation: $reportPath"
    }
    foreach ($check in $result.checks) {
        if ($check.Passed -ne $true) { throw "UI check failed: $($check.Name). Report: $reportPath" }
        Write-Host "PASS: $($check.Name)" -ForegroundColor Green
    }
    Write-Host "PASS: RC2 portable EXE opened all smoke-tested WPF windows. Report: $reportPath" -ForegroundColor Green
    Write-Warning 'This automated GUI smoke check is NOT interactive user acceptance. v1.0.0 stable is NOT approved.'
}
finally {
    if ($proc -and -not $proc.HasExited) { try { $proc.Kill() } catch {} }
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}


$baseDir  = Join-Path $env:LOCALAPPDATA "AroniumBridge"
$settings = Join-Path $baseDir "appsettings.json"
$diagFile = Join-Path $baseDir "startup_diag.txt"
$vportLog = Join-Path $baseDir "vport_log.txt"

Write-Host ""
Write-Host "=== AroniumBridge Diagnostic ===" -ForegroundColor Cyan
Write-Host ""

$proc = Get-Process -Name "AroniumBridge" -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "PROCESS: RUNNING (PID $($proc.Id))" -ForegroundColor Green
} else {
    Write-Host "PROCESS: NOT RUNNING" -ForegroundColor Red
}

Write-Host ""
Write-Host "--- startup_diag.txt ---" -ForegroundColor Yellow
if (Test-Path $diagFile) {
    Get-Content $diagFile
} else {
    Write-Host "NOT FOUND"
}

Write-Host ""
Write-Host "--- appsettings.json ---" -ForegroundColor Yellow
if (Test-Path $settings) {
    Get-Content $settings
} else {
    Write-Host "NOT FOUND"
}

Write-Host ""
Write-Host "--- vport_log.txt (last 40 lines) ---" -ForegroundColor Yellow
if (Test-Path $vportLog) {
    Get-Content $vportLog | Select-Object -Last 40
} else {
    Write-Host "NOT FOUND"
}

Write-Host ""
Write-Host "--- COM Ports ---" -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Ports
[System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object | ForEach-Object { Write-Host $_ }
Write-Host ""

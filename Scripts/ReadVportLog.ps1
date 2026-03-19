# ReadVportLog.ps1
# Run this after doing a test sale in Aronium to see exactly what was captured.
# Usage: powershell -ExecutionPolicy Bypass -File "K:\Aronium Bridge\ReadVportLog.ps1"

$logPath = Join-Path $env:LOCALAPPDATA "AroniumBridge\vport_log.txt"

if (-not (Test-Path $logPath)) {
    Write-Host "Log file not found at: $logPath" -ForegroundColor Red
    Write-Host "Make sure AroniumBridge is running in Virtual Port mode and you've done a sale."
    exit
}

Write-Host "=== VFD Capture Log ===" -ForegroundColor Cyan
Write-Host "File: $logPath" -ForegroundColor Gray
Write-Host ""
Get-Content $logPath | ForEach-Object { Write-Host $_ }

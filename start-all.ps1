param(
    [string]$PythonProject = $(if (Test-Path (Join-Path (Split-Path $PSScriptRoot -Parent) "HealthTech")) { Join-Path (Split-Path $PSScriptRoot -Parent) "HealthTech" } else { Join-Path $PSScriptRoot "..\HealthTech" })
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path (Join-Path $PythonProject "server.py"))) {
    throw "Python project not found. Pass -PythonProject <path> to this script."
}

Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PSScriptRoot'; dotnet run --urls http://127.0.0.1:8080"
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PythonProject'; python -m uvicorn server:app --host 127.0.0.1 --port 8000"
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$PSScriptRoot\Frontend'; npm.cmd install; npm.cmd run dev"

Write-Host "React:  http://127.0.0.1:5173/"
Write-Host "Python: http://127.0.0.1:8000/health"
Write-Host ".NET:   http://127.0.0.1:8080/api/health"

# PATCHY API and React Application

PATCHY is a .NET 8 backend with a React + TypeScript + Vite frontend.

The YOLO WebSocket integration is maintained in the separate HealthTech repository. This repository contains the .NET API and React application.

## Prerequisites

- .NET 8 SDK
- Node.js/npm
- SQL Server Express with instance `SQLEXPRESS`

## Start .NET API

```powershell
cd /path/to/Patchy.Api
$env:Path += ';C:\Program Files\dotnet'
dotnet restore
dotnet run --urls http://127.0.0.1:8080
```

API health: `http://127.0.0.1:8080/api/health`

## Start React frontend

```powershell
cd /path/to/Patchy.Api/Frontend
npm install
npm run dev
```

Frontend: `http://127.0.0.1:5173/`

Vite proxies `/api` to the .NET API and `/ws-python` to the Python WebSocket server.

## Configuration

- .NET database and JWT settings: `appsettings.json`
- Frontend API/Python settings: `Frontend/.env.example`
- SQL Server connection defaults to `server=.\SQLEXPRESS;Database=LazyEya`

## All-in-one launcher

```powershell
PowerShell -ExecutionPolicy Bypass -File .\start-all.ps1 -PythonProject "C:\path\to\HealthTech"
```

The launcher starts the .NET API, Python YOLO server, and React frontend in separate PowerShell windows.

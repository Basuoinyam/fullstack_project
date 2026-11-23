# PATCHY API and Frontend Integration Changes

## React frontend

- Added `Frontend/` as a complete Vite React/TypeScript application.
- Preserved the existing `.NET /api` proxy and authentication flow.
- Added `/ws-python` proxy routing to the separate Python server on port 8000.
- Reused the existing `getUserMedia()` and `videoRef` camera implementation.
- Captured frames through Canvas and sent one frame at a time over WebSocket.
- Added YOLO annotated-frame and detection display to camera monitoring and today's quest.
- Added WebSocket reconnection and frame timeout handling.
- Added JWT Bearer header handling in the API client.
- Added cleanup for camera streams, WebSockets, and Canvas resources.

## .NET backend

- Remains the source of authentication and database-backed session data on port 8080.
- SQL Server Express database: `LazyEya`.
- Session start and completion endpoints persist results in SQL Server.

## Deliverables

- `Frontend/.env.example`
- `start-all.ps1`
- Updated root `.gitignore`
- Updated root `README.md`

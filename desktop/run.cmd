@echo off
rem ============================================================================
rem  Run the native Windows island.
rem
rem  The window is topmost, transparent outside the capsule, and click-through
rem  everywhere the capsule is not. There is no taskbar button and no tray icon;
rem  close it with the X inside the expanded card, or:
rem    taskkill /im WinIsland.exe /f
rem ============================================================================
if not exist "%~dp0WinIsland.exe" (
  echo [x] WinIsland.exe missing - run build.cmd first
  exit /b 1
)
start "" "%~dp0WinIsland.exe" %*
exit /b 0
@echo off
rem ============================================================================
rem  Build and run the todo-tower physics tests.
rem
rem  Compiles desktop\Tower.cs (the ported 2D rigid-body solver) together with
rem  verify\tower-tests.cs. No window, no user data, no WPF types are touched:
rem  the only source under test is the WPF-free physics file, so this runs
rem  headless and deterministic.
rem
rem  Asserts: the stack stands at the right heights and does not creep, pulling
rem  a card out drops everything above it by one card, a carried card can be
rem  moved across the room and released without being lost, a 4000px/s throw is
rem  clamped and returns inside the room, an empty room steps without error, and
rem  an overhanging card really tips over (torque, not a tween).
rem
rem  Run from anywhere. Exit code 0 = every assert passed.
rem
rem  NOTE: keep this file ASCII-only (cmd.exe reads .cmd as the OEM codepage).
rem ============================================================================
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"

if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  exit /b 1
)

pushd "%~dp0.."

"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0tower-tests.exe" ^
  /main:WinIsland.TowerTests ^
  /codepage:65001 ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Core.dll" ^
  Tower.cs ^
  "%~dp0tower-tests.cs"

set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo [x] tower-tests build failed with %RC%
  exit /b %RC%
)

echo [ok] tower-tests.exe built
echo.
"%~dp0tower-tests.exe"
set "TRC=%ERRORLEVEL%"
echo.
echo [tower-tests exit=%TRC%]
exit /b %TRC%
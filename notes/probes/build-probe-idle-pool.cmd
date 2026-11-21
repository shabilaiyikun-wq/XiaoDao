@echo off
rem ============================================================================
rem  Build + run the random-idle-pool probe.
rem
rem  It compiles the shared animation sources only -- the same list as
rem  pet-demo\test.cmd, minus the test harness -- so it needs no desktop
rem  sources, opens no window, and never touches ui.txt / window.txt / pet.txt.
rem  That is why it can run next to the coexistence probe without backups.
rem
rem  What it asserts: pool composition (34 = 7 authored + 27 reference),
rem  Blocked_A excluded, every pooled clip samples and renders, three real
rem  shuffle cycles play them all with no adjacent repeats, and the manual /
rem  expression / poke / spin paths are untouched.
rem
rem  NOTE: keep this file ASCII-only (cmd.exe reads .cmd as the OEM codepage).
rem ============================================================================
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
set "CSC=%FW%\csc.exe"
set "PET=%~dp0..\..\pet-demo"

if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  exit /b 1
)

pushd "%~dp0"

"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0probe-idle-pool.exe" ^
  /main:WinIsland.ProbeIdlePool ^
  /codepage:65001 ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.dll" ^
  /resource:"%PET%\grok-clips.json",WinIsland.grok-clips.json ^
  "%PET%\AnimationCore.cs" ^
  "%PET%\PetClips.cs" ^
  "%PET%\ReferenceClips.cs" ^
  "%PET%\PetShape.cs" ^
  "%PET%\PetAnim.cs" ^
  "%PET%\Pet3D.cs" ^
  "%~dp0probe-idle-pool.cs"

set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo [x] probe build failed with %RC%
  exit /b %RC%
)

echo [ok] probe-idle-pool.exe built
echo.
"%~dp0probe-idle-pool.exe"
set "PRC=%ERRORLEVEL%"
echo.
echo [probe exit=%PRC%]
exit /b %PRC%
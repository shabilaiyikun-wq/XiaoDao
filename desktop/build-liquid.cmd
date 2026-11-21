@echo off
rem ============================================================================
rem  Build the ISOLATED liquid-neck verification probe.
rem
rem  This is deliberately NOT part of build.cmd's WinIsland.exe: the prototype is
rem  an isolated experiment, so it gets its own tiny console binary that compiles
rem  only the geometry + tension + probe sources. No island, no pet, no WinRT, no
rem  resource embedding -- if this builds, the geometry is testable on its own.
rem
rem  Toolchain is the same as build.cmd: the C# 5 compiler from .NET Framework 4.8.
rem  Keep this file ASCII-only (cmd.exe reads .cmd as the OEM codepage).
rem
rem  Usage:  build-liquid.cmd            -> LiquidProbe.exe next to this script
rem          build-liquid.cmd /run DIR   -> build, then run it into DIR
rem ============================================================================
setlocal

set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"

if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  exit /b 1
)

pushd "%~dp0"

"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /out:LiquidProbe.exe ^
  /codepage:65001 ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.dll" ^
  LiquidPair.cs ^
  LiquidGeometry.cs ^
  LiquidTension.cs ^
  LiquidProbe.cs ^
  LiquidProbeMain.cs

set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo [x] build failed with %RC%
  exit /b %RC%
)
echo [ok] LiquidProbe.exe built

if /i not "%~1"=="/run" exit /b 0

set "OUTDIR=%~2"
if not defined OUTDIR set "OUTDIR=%~dp0work\liquid-probe"
"%~dp0LiquidProbe.exe" --out "%OUTDIR%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo [x] probe failed with %RC%
  exit /b %RC%
)
echo [ok] liquid probe passed
exit /b 0

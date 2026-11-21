@echo off
rem ============================================================================
rem  Build + run the press-squish probe.
rem
rem  It compiles the real desktop sources (the same ones WinIsland.exe uses) plus
rem  the shared pet animation sources, because the probe drives a REAL
rem  Layout.Build card -- fake buttons would prove nothing about hit rects,
rem  layout sizes or the OnTap wiring.
rem
rem  Usage:
rem    build-probe-squish.cmd              build + run every case
rem    build-probe-squish.cmd render OUT   build + render one still frame to OUT
rem
rem  NOTE: keep this file ASCII-only (cmd.exe reads .cmd as the OEM codepage).
rem ============================================================================
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
set "CSC=%FW%\csc.exe"
set "PET=%~dp0..\..\pet-demo"
set "DESK=%~dp0..\..\desktop"
set "WINMD=%WINDIR%\System32\WinMetadata"

if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  exit /b 1
)

pushd "%~dp0"

"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0probe-squish.exe" ^
  /main:WinIsland.ProbeSquish ^
  /codepage:65001 ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.Drawing.dll" ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%WINMD%\Windows.Foundation.winmd" ^
  /r:"%WINMD%\Windows.Media.winmd" ^
  /r:"%WINMD%\Windows.Storage.winmd" ^
  /resource:"%PET%\grok-clips.json",WinIsland.grok-clips.json ^
  "%DESK%\Native.cs" ^
  "%DESK%\Squircle.cs" ^
  "%DESK%\Squish.cs" ^
  "%DESK%\Scenes.cs" ^
  "%DESK%\Morph.cs" ^
  "%DESK%\Glass.cs" ^
  "%DESK%\LiquidTension.cs" ^
  "%DESK%\LiquidPair.cs" ^
  "%DESK%\LiquidGeometry.cs" ^
  "%DESK%\Theme.cs" ^
  "%DESK%\Ui.cs" ^
  "%DESK%\Data.cs" ^
  "%DESK%\Layout.cs" ^
  "%DESK%\Menu.cs" ^
  "%DESK%\Media.cs" ^
  "%DESK%\Lyric.cs" ^
  "%DESK%\Store.cs" ^
  "%DESK%\Transfer.cs" ^
  "%DESK%\Notify.cs" ^
  "%DESK%\Input.cs" ^
  "%DESK%\Shot.cs" ^
  "%PET%\AnimationCore.cs" ^
  "%PET%\PetGaze.cs" ^
  "%PET%\PetClips.cs" ^
  "%PET%\ReferenceClips.cs" ^
  "%PET%\PetShape.cs" ^
  "%PET%\PetAnim.cs" ^
  "%PET%\Pet3D.cs" ^
  "%PET%\PetStage.cs" ^
  "%PET%\Pet.cs" ^
  "%~dp0probe-squish.cs"

set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo [x] probe build failed with %RC%
  exit /b %RC%
)

echo [ok] probe-squish.exe built
echo.

if /i "%~1"=="render" (
  pushd "%~dp0"
  "%~dp0probe-squish.exe" render %2
  set "PRC=%ERRORLEVEL%"
  popd
  echo [render exit=%PRC%]
  exit /b %PRC%
)

if /i "%~1"=="centers" (
  pushd "%~dp0"
  "%~dp0probe-squish.exe" centers %2 %3 %4
  set "PRC=%ERRORLEVEL%"
  popd
  echo [centers exit=%PRC%]
  exit /b %PRC%
)

pushd "%~dp0"
"%~dp0probe-squish.exe"
set "PRC=%ERRORLEVEL%"
popd
echo.
echo [probe exit=%PRC%]
exit /b %PRC%
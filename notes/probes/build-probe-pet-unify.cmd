@echo off
rem ============================================================================
rem  Build + run the pet/island coexistence probe.
rem
rem  It compiles the real desktop sources (every .cs that desktop\build.cmd
rem  compiles, minus Program.cs -- the probe supplies its own entry point via
rem  /main:) together with the pet sources from ..\pet-demo, plus the probe.
rem
rem  Why not just add it to build.cmd: WinIsland.exe is a winexe with no console,
rem  and this needs Console output plus a non-zero exit code. Same reason
rem  probe-petclose.cs is built by hand.
rem
rem  NOTE: keep this file ASCII-only (cmd.exe reads .cmd as the OEM codepage).
rem ============================================================================
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
set "CSC=%FW%\csc.exe"
set "WINMD=%WINDIR%\System32\WinMetadata"
set "DESK=%~dp0..\..\desktop"
set "PET=%~dp0..\..\pet-demo"

if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  exit /b 1
)

pushd "%DESK%"

if exist WinIsland.exe (
  tasklist /fi "imagename eq WinIsland.exe" 2>nul | find /i "WinIsland.exe" >nul
  if not errorlevel 1 (
    echo [x] WinIsland.exe is running - close it first, otherwise the probe
    echo     fights the real instance over pet.txt / window.txt / ui.txt
    popd
    exit /b 1
  )
)

"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0probe-pet-unify.exe" ^
  /main:WinIsland.ProbePetUnify ^
  /codepage:65001 ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.Drawing.dll" ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  /r:"%WINMD%\Windows.Foundation.winmd" ^
  /r:"%WINMD%\Windows.Media.winmd" ^
  /r:"%WINMD%\Windows.Storage.winmd" ^
  /resource:"%PET%\grok-clips.json",WinIsland.grok-clips.json ^
  Native.cs ^
  Squircle.cs ^
  Theme.cs ^
  Ui.cs ^
  Data.cs ^
  Layout.cs ^
  Menu.cs ^
  Media.cs ^
  Lyric.cs ^
  Store.cs ^
  Transfer.cs ^
  Notify.cs ^
  Input.cs ^
  Shot.cs ^
  PetController.cs ^
  "%PET%\AnimationCore.cs" ^
  "%PET%\PetClips.cs" ^
  "%PET%\ReferenceClips.cs" ^
  "%PET%\PetShape.cs" ^
  "%PET%\PetAnim.cs" ^
  "%PET%\Pet3D.cs" ^
  "%PET%\Pet.cs" ^
  IslandWindow.cs ^
  "%~dp0probe-pet-unify.cs"

set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo [x] probe build failed with %RC%
  exit /b %RC%
)

echo [ok] probe-pet-unify.exe built
echo.
"%~dp0probe-pet-unify.exe"
set "PRC=%ERRORLEVEL%"
echo.
echo [probe exit=%PRC%]
exit /b %PRC%
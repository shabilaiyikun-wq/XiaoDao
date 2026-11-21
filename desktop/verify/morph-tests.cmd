@echo off
rem ============================================================================
rem  Offline verification for the island morph / layer transition.
rem
rem  Compiles ONLY the stable subset the morph logic depends on:
rem      ..\Squircle.cs   (Spring: the shared integrator)
rem      ..\Scenes.cs     (layer size table, shared with the app)
rem      ..\Morph.cs      (MorphBody / FaceBook / IslandFrame / PetLink)
rem      morph-tests.cs   (assertions)
rem
rem  Deliberately NOT the whole app: no WPF window, no DPI, no desktop. That
rem  keeps the numbers identical on every machine and keeps this suite immune to
rem  in-flight edits elsewhere in the tree.
rem
rem  Exit code = number of failed assertions (0 = all good).
rem ============================================================================
setlocal

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
set "CSC=%FW%\csc.exe"
set "OUT=%~dp0morph-tests.exe"

if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  exit /b 1
)

"%CSC%" /nologo /codepage:65001 /target:exe /out:"%OUT%" ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  "%~dp0..\Squircle.cs" ^
  "%~dp0..\Scenes.cs" ^
  "%~dp0..\Morph.cs" ^
  "%~dp0morph-tests.cs"

if errorlevel 1 (
  echo [x] compile failed
  exit /b 1
)

"%OUT%"
set "RC=%ERRORLEVEL%"
if %RC% equ 0 (echo [ok] morph-tests passed) else (echo [x] morph-tests failed with %RC% failed assertion)
exit /b %RC%
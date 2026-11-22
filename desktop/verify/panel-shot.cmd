@echo off
rem ============================================================================
rem  Render the island's clipboard and transit pages to PNG files, offline.
rem
rem  Builds the real Layout tree for scene "clip" and scene "transfer" at
rem  468x268, arranges it once and rasterises it with RenderTargetBitmap. No
rem  window is shown, and AppDir is pointed at a temp sandbox with fabricated
rem  data, so the user's real clipboard history / transit list is never read or
rem  written.
rem
rem  Output (default work\panel-shot\):
rem    01-clip.png            six clipboard entries (multi-line, over-long)
rem    02-clip-frost.png      the same page under the light look
rem    03-clip-empty.png      empty clipboard
rem    04-transfer.png        twelve entries (folder, real .exe, one missing)
rem    05-transfer-frost.png
rem    06-transfer-hover.png  drag hovering -> the whole area becomes a drop zone
rem    07-transfer-empty.png
rem
rem  It also asserts the things a picture cannot say by itself: bar count,
rem  pitch, and the bar material compared against a real todo card built in the
rem  same look; the icon grid's cell geometry; that every icon is a large
rem  bitmap; and that each cell really painted ink.
rem
rem  Usage: panel-shot.cmd [output-dir]
rem  Exit code 0 = every page rendered and every assertion held.
rem
rem  SOURCE LIST: collected by walking desktop\*.cs and skipping the files that
rem  cannot live in a headless probe -- the app entry point, the window itself,
rem  the desktop pet (it needs ..\pet-demo and embedded resources) and the
rem  isolated liquid probe's own Main. Collecting beats listing: a hand-kept
rem  copy of build.cmd's list drifts the moment someone adds a file.
rem
rem  NOTE: keep this file ASCII-only (cmd.exe reads .cmd as the OEM codepage).
rem ============================================================================
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"
set "WINMD=%WINDIR%\System32\WinMetadata"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  exit /b 1
)
if not exist "%WINMD%\Windows.Media.winmd" (
  echo [x] WinRT metadata not found at "%WINMD%"
  exit /b 1
)

if "%~1"=="" (
  set "OUT=%~dp0..\..\work\panel-shot"
) else (
  set "OUT=%~1"
)

pushd "%~dp0.."

set "SRCS="
for %%F in (*.cs) do (
  set "SKIP="
  if /i "%%F"=="Program.cs"        set "SKIP=1"
  if /i "%%F"=="IslandWindow.cs"   set "SKIP=1"
  if /i "%%F"=="PetController.cs"  set "SKIP=1"
  if /i "%%F"=="PetIsland.cs"      set "SKIP=1"
  if /i "%%F"=="PetReaction.cs"    set "SKIP=1"
  if /i "%%F"=="Pet.cs"            set "SKIP=1"
  if /i "%%F"=="PetAnim.cs"        set "SKIP=1"
  if /i "%%F"=="PetShape.cs"       set "SKIP=1"
  if /i "%%F"=="LiquidProbeMain.cs" set "SKIP=1"
  rem Menu.cs builds the pet clip submenu and needs ..\pet-demo's ReferenceClips;
  rem neither of these two pages touches it.
  if /i "%%F"=="Menu.cs"          set "SKIP=1"
  if not defined SKIP call set "SRCS=%%SRCS%% %%F"
)

"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0panel-shot.exe" ^
  /main:WinIsland.PanelShot ^
  /codepage:65001 ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%WPF%\UIAutomationClient.dll" ^
  /r:"%WPF%\UIAutomationTypes.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.Drawing.dll" ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  /r:"%WINMD%\Windows.Foundation.winmd" ^
  /r:"%WINMD%\Windows.Media.winmd" ^
  /r:"%WINMD%\Windows.Storage.winmd" ^
  %SRCS% ^
  "%~dp0panel-shot.cs"

set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" (
  echo [x] panel-shot build failed with %RC%
  exit /b %RC%
)

echo [ok] panel-shot.exe built
echo.
"%~dp0panel-shot.exe" "%OUT%"
set "SRC=%ERRORLEVEL%"
echo.
echo [panel-shot exit=%SRC%]
exit /b %SRC%
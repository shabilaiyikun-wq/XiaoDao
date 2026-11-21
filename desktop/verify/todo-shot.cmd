@echo off
rem ============================================================================
rem  Render the island's todo page to PNG files, offline.
rem
rem  Builds the real Layout tree for scene "todo"/state "expanded" at 468x292,
rem  arranges it once and rasterises it with RenderTargetBitmap. No window is
rem  shown, and AppDir is pointed at a temp sandbox so the user's real todos are
rem  never read or written.
rem
rem  Output (default work\todo-shot\):
rem    01-open.png    normal todos, one overdue and one due soon
rem    02-done.png    the "done" stack (struck-through text)
rem    03-empty.png   empty list
rem    04-single.png  a single card
rem
rem  Usage: todo-shot.cmd [output-dir]
rem  Exit code 0 = every state rendered and its card count matched.
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
  set "OUT=%~dp0..\..\work\todo-shot"
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
  rem the todo page never touches it.
  if /i "%%F"=="Menu.cs"          set "SKIP=1"
  if not defined SKIP call set "SRCS=%%SRCS%% %%F"
)

"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0todo-shot.exe" ^
  /main:WinIsland.TodoShot ^
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
  "%~dp0todo-shot.cs"

set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" (
  echo [x] todo-shot build failed with %RC%
  exit /b %RC%
)

echo [ok] todo-shot.exe built
echo.
"%~dp0todo-shot.exe" "%OUT%"
set "SRC=%ERRORLEVEL%"
echo.
echo [todo-shot exit=%SRC%]
exit /b %SRC%
@echo off
rem ============================================================================
rem  Render the island's weather page to PNG files, offline.
rem
rem  Builds the real Layout tree for scene "weather"/state "expanded" at
rem  468x268, arranges it once and rasterises it with RenderTargetBitmap.
rem  No window is shown, no network is touched, and AppDir points at a temp
rem  sandbox so the user's real weather cache is never read or written.
rem
rem  It also checks the pure logic that is easy to get silently wrong:
rem  WMO code to Chinese text and icon kind, and the Chinese city-name
rem  rewriting (for example "Guangzhou City" collapses to "Guangzhou",
rem  and "Xinjiang" also tries the provincial capital "Urumqi").
rem
rem  Output (default work\weather-shot\):
rem    01-card-clear.png     sunny, 12 degrees
rem    02-card-rain.png      moderate rain
rem    03-card-snow.png      moderate snow
rem    04-card-thunder.png   thunderstorm
rem    05-card-night.png     clear at night (moon icon)
rem    06-empty.png          no data yet
rem    07-idle-weather.png   collapsed island: orb + icon + temperature
rem    08-idle-noweather.png collapsed island without weather (must have no text)
rem    09-icons.png          all eleven icon kinds side by side
rem
rem  Usage: weather-shot.cmd [output-dir]
rem  Exit code 0 = every assertion passed.
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
  set "OUT=%~dp0..\..\work\weather-shot"
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
  rem the weather page never touches it.
  if /i "%%F"=="Menu.cs"          set "SKIP=1"
  if not defined SKIP call set "SRCS=%%SRCS%% %%F"
)

"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0weather-shot.exe" ^
  /main:WinIsland.WeatherShot ^
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
  "%~dp0weather-shot.cs"

set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" (
  echo [x] weather-shot build failed with %RC%
  exit /b %RC%
)

echo [ok] weather-shot.exe built
echo.
"%~dp0weather-shot.exe" "%OUT%"
set "SRC=%ERRORLEVEL%"
echo.
echo [weather-shot exit=%SRC%]
exit /b %SRC%
@echo off
rem ============================================================================
rem  Build the native Windows island.
rem
rem  Toolchain: the C# 5 compiler that ships with .NET Framework 4.8. No SDK,
rem  no MSBuild, no NuGet. Everything is compiled from source in one step, so
rem  the only requirement is a stock Windows 10/11 install.
rem
rem  NOTE: keep this file ASCII-only. cmd.exe reads .cmd as the OEM codepage,
rem  so non-ASCII bytes here corrupt the following commands.
rem
rem  WinRT metadata: no Windows SDK on the box, so we reference the runtime
rem  metadata in %WINDIR%\System32\WinMetadata. Do NOT also reference
rem  System.Runtime.WindowsRuntime -- its AsTask is compiled against the merged
rem  Windows.winmd and will not match the split files; Media.cs polls
rem  IAsyncOperation.Status and calls GetResults instead.
rem
rem  PET SOURCES: the desk pet lives in ..\pet-demo and is compiled from there,
rem  in place. desktop\Pet.cs, desktop\PetAnim.cs and desktop\PetShape.cs are the
rem  old implementation -- they are DEAD FILES, kept only for history, and must
rem  NOT come back into this list. One source tree, one behaviour, no copies.
rem  pet-demo\DemoSupport.cs and pet-demo\Program.cs are deliberately excluded:
rem  they redeclare Native/Ui/AppDir and a second Program class.
rem ============================================================================
setlocal

set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FW%\csc.exe"
set "WINMD=%WINDIR%\System32\WinMetadata"
set "PET=..\pet-demo"

if not exist "%CSC%" (
  echo [x] csc.exe not found at "%CSC%"
  echo     .NET Framework 4.x is required.
  exit /b 1
)
if not exist "%WPF%\PresentationFramework.dll" (
  echo [x] WPF assemblies not found at "%WPF%"
  exit /b 1
)
if not exist "%WINMD%\Windows.Media.winmd" (
  echo [x] WinRT metadata not found at "%WINMD%"
  exit /b 1
)

pushd "%~dp0"

rem The pet is a sibling tree; fail early and clearly if it is missing, instead
rem of letting csc report CS2001 for seven files plus a missing resource.
for %%F in (AnimationCore.cs PetClips.cs ReferenceClips.cs PetShape.cs PetAnim.cs Pet3D.cs Pet.cs grok-clips.json) do (
  if not exist "%PET%\%%F" (
    echo [x] missing %PET%\%%F
    popd
    exit /b 1
  )
)

rem /resource: embeds the 28 reference clip recipes as WinIsland.grok-clips.json.
rem ReferenceClips reads that exact name out of the assembly manifest, so it has
rem to stay identical in both build scripts.
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:WinIsland.exe ^
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
  "%PET%\AnimationCore.cs" ^
  "%PET%\PetClips.cs" ^
  "%PET%\ReferenceClips.cs" ^
  "%PET%\PetShape.cs" ^
  "%PET%\PetAnim.cs" ^
  "%PET%\Pet3D.cs" ^
  "%PET%\PetStage.cs" ^
  "%PET%\Pet.cs" ^
  PetController.cs ^
  IslandWindow.cs ^
  PetIsland.cs ^
  Program.cs

set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo [x] build failed with %RC%
  exit /b %RC%
)
echo [ok] WinIsland.exe built
exit /b 0

@echo off
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo [x] .NET Framework 4.x csc.exe not found
  exit /b 1
)
pushd "%~dp0"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:PetDemo.exe ^
  /codepage:65001 ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Drawing.dll" ^
  /r:"%FW%\System.Windows.Forms.dll" ^
  /resource:grok-clips.json,WinIsland.grok-clips.json ^
  DemoSupport.cs AnimationCore.cs PetGaze.cs PetClips.cs ReferenceClips.cs PetShape.cs PetAnim.cs Pet3D.cs PetStage.cs Pet.cs Program.cs
set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" exit /b %RC%
echo [ok] PetDemo.exe built
exit /b 0

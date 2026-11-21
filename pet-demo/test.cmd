@echo off
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "WPF=%FW%\WPF"
pushd "%~dp0"
"%FW%\csc.exe" /nologo /target:exe /platform:x64 /optimize+ /out:AnimationTests.exe ^
  /codepage:65001 ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  /r:"%FW%\System.Core.dll" /r:"%FW%\System.dll" ^
  /resource:grok-clips.json,WinIsland.grok-clips.json ^
  AnimationCore.cs PetGaze.cs PetClips.cs ReferenceClips.cs PetShape.cs PetAnim.cs Pet3D.cs AnimationTests.cs
if errorlevel 1 (popd & exit /b 1)
AnimationTests.exe
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%

@echo off
if not exist "%~dp0PetDemo.exe" (
  call "%~dp0build.cmd"
  if errorlevel 1 exit /b 1
)
start "" "%~dp0PetDemo.exe" %*

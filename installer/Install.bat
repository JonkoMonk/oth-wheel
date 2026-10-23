@echo off
if not exist "%~dp0files\installer.ps1" (
  echo.
  echo  Please extract the zip first:
  echo  right-click the zip file, choose "Extract All...", then run Install.bat
  echo  from the extracted folder.
  echo.
  pause
  exit /b 1
)
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0files\installer.ps1"

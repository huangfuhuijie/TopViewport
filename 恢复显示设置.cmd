@echo off
cd /d "%~dp0"
start "" /wait "%~dp0TopViewport.exe" --recover
if errorlevel 1 (
  echo Recovery failed. See %%LOCALAPPDATA%%\TopViewport\TopViewport.log
  pause
)

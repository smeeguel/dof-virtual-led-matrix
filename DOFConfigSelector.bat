@echo off
setlocal

REM Backward-compatible launcher: keep old filename working while setup logic lives in DOFConfigSetup.bat.
set "scriptDir=%~dp0"
set "setupScript=%scriptDir%DOFConfigSetup.bat"

if not exist "%setupScript%" (
    echo Error: DOFConfigSetup.bat was not found next to this launcher.
    pause
    exit /b 1
)

call "%setupScript%" %*
exit /b %errorlevel%

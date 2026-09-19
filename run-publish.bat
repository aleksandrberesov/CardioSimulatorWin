@echo off
setlocal enabledelayedexpansion

:: Set working directory to repository root
cd /d "%~dp0"

set "PUBLISH_DIR=%~dp0artifacts\publish"
set "TARGET_EXE=%PUBLISH_DIR%\antiAI-ECG-Simulator.exe"

if not exist "%TARGET_EXE%" (
    if exist "%PUBLISH_DIR%\CardioSimulator.exe" (
        set "TARGET_EXE=%PUBLISH_DIR%\CardioSimulator.exe"
    ) else (
        echo [ERROR] Application executable not found in %PUBLISH_DIR%
        echo Please build and publish the project first, for example:
        echo   powershell -ExecutionPolicy Bypass -File .\run.ps1 -Publish
        echo   OR
        echo   powershell -ExecutionPolicy Bypass -File .\tools\build.ps1 -Publish
        echo.
        pause
        exit /b 1
    )
)

echo ========================================================
echo Launching published CardioSimulator...
echo Path: %TARGET_EXE%
echo ========================================================

pushd "%PUBLISH_DIR%"
start "" "%TARGET_EXE%" %*
popd

endlocal

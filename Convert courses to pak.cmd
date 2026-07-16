@echo off
rem ---------------------------------------------------------------------------
rem  Cardio Simulator - convert an old Courses ZIP into the new .pak format.
rem
rem  For customers: drag your Courses ZIP onto this file, or just double-click it
rem  and it will ask which file to convert.
rem
rem  This wrapper exists because Windows blocks .ps1 scripts by default
rem  (execution policy), and because double-clicking a .ps1 opens it in Notepad
rem  rather than running it. -ExecutionPolicy Bypass applies to THIS run only and
rem  changes nothing on the machine.
rem ---------------------------------------------------------------------------

setlocal
set "SCRIPT=%~dp0convert-courses-to-pak.ps1"

if not exist "%SCRIPT%" (
    echo.
    echo   Setup problem: convert-courses-to-pak.ps1 is missing.
    echo   Keep this file in the folder your supplier sent you.
    echo.
    pause
    exit /b 1
)

rem %~1 is the file dragged onto this icon; empty on a plain double-click, in
rem which case the script prompts for the path itself.
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %1

echo.
pause
endlocal

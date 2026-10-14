@echo off
setlocal
rem ============================================================
rem  Build script for MCHOSE A7 Pro Battery Monitor
rem  Uses the C# compiler (csc.exe) built into Windows itself.
rem  No SDK / Visual Studio / third-party library required.
rem  NOTE: keep this file ASCII-only to avoid cmd encoding issues.
rem ============================================================

set "NAME=MCHOSE_A7Pro_BatteryMonitor.exe"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] Built-in C# compiler not found. Windows 10 / 11 required.
    pause
    exit /b 1
)

echo Compiler: %CSC%
echo.

rem ---- step 1: stop a running instance, otherwise the exe is file-locked ----
taskkill /IM "%NAME%" /F >nul 2>&1
if not errorlevel 1 (
    echo Stopped the running instance so the exe can be replaced.
    ping -n 3 127.0.0.1 >nul
)

rem ---- step 2: compile into a staging file first ----
set "TMPOUT=%~dp0MCHOSE_A7Pro_BatteryMonitor.new.exe"
if exist "%TMPOUT%" del /F /Q "%TMPOUT%" >nul 2>&1

"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /out:"%TMPOUT%" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%~dp0src\Program.cs" "%~dp0src\Settings.cs" "%~dp0src\HidBatteryReader.cs" "%~dp0src\MainContext.cs" "%~dp0src\SettingsForm.cs"

if errorlevel 1 (
    echo.
    echo [FAILED] Build error. Please send the messages above to the developer.
    pause
    exit /b 1
)

rem ---- step 3: swap the staging file over the real target ----
move /Y "%TMPOUT%" "%~dp0%NAME%" >nul 2>&1
if errorlevel 1 (
    echo.
    echo [FAILED] Compiled OK, but the old exe is still locked by another program.
    echo Right-click the tray icon and choose Exit, then run this script again.
    echo Staged file left at: %TMPOUT%
    pause
    exit /b 1
)

echo.
echo [OK] Built successfully: %~dp0%NAME%
echo Double-click the exe to run it. It will live in the system tray.
echo If you do not see the tray icon, check the hidden tray overflow area.
echo.
choice /c YN /m "Start it now? [Y/N]"
if errorlevel 2 exit /b 0
start "" "%~dp0%NAME%"
exit /b 0

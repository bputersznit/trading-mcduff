@echo off
REM Deploy latest strategy to NinjaTrader 8
REM Run this from Windows VM

echo =========================================
echo CG Strategy Deployment to NinjaTrader 8
echo =========================================
echo.

REM Set source (VirtualBox shared folder)
set SOURCE=\\VBOXSVR\CG_MNQ_MarketReplayLab\ninjascript\CG_OrderFlow_Aggression_v2_8_STAGE2_DISCOVERY_RESPONSE.cs

REM Set destination (NinjaTrader Custom Strategies)
set DEST=%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Strategies\

echo Source: %SOURCE%
echo Destination: %DEST%
echo.

REM Check if source file exists
if not exist "%SOURCE%" (
    echo ERROR: Source file not found!
    echo Make sure VirtualBox shared folder is mounted.
    pause
    exit /b 1
)

REM Check if destination directory exists
if not exist "%DEST%" (
    echo ERROR: NinjaTrader Strategies directory not found!
    echo Please verify NinjaTrader 8 is installed.
    pause
    exit /b 1
)

REM Copy file
echo Copying strategy file...
copy /Y "%SOURCE%" "%DEST%"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo SUCCESS: Strategy deployed to NinjaTrader!
    echo.
    echo Next steps:
    echo 1. Open NinjaTrader 8
    echo 2. Press F5 to compile
    echo 3. Look for CG_OrderFlow_Aggression_v2_8_STAGE2_DISCOVERY_RESPONSE in Strategy list
    echo.
) else (
    echo.
    echo ERROR: Failed to copy file.
    echo.
)

pause

@echo off
REM L2 Watcher Startup Script for Windows VPS
REM Place this file in C:\Users\Administrator\ along with vps_l2_watcher.py

echo ========================================
echo Starting L2 CSV Watcher
echo ========================================
echo.

cd C:\Users\Administrator

REM Check Python
python --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python not found in PATH
    echo Please install Python from python.org
    pause
    exit /b 1
)

REM Check dependencies
echo Checking dependencies...
python -c "import pandas, pyarrow" >nul 2>&1
if errorlevel 1 (
    echo Installing required packages...
    pip install pandas pyarrow
)

REM Check rclone
echo Checking rclone...
rclone version >nul 2>&1
if errorlevel 1 (
    echo ERROR: rclone not found
    echo Download from: https://rclone.org/downloads/
    pause
    exit /b 1
)

REM Test rclone connection
echo Testing Ubuntu connection...
rclone lsd ubuntu:/home/bernard/ >nul 2>&1
if errorlevel 1 (
    echo WARNING: Cannot connect to Ubuntu
    echo Please configure rclone first: rclone config
    echo.
    echo Press any key to continue anyway...
    pause >nul
)

REM Start watcher
echo.
echo Starting watcher...
echo Monitoring: C:\Users\Administrator\Documents\CG_L2_Capture
echo Logs: l2_watcher.log
echo.
echo Press Ctrl+C to stop
echo ========================================
echo.

python vps_l2_watcher.py

pause

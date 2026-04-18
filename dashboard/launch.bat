@echo off
echo Ostranauts Companion Dashboard
echo ===============================
echo.
echo Make sure the game is running with CompanionServer plugin loaded.
echo Dashboard will be at: http://localhost:8086
echo.
cd /d "%~dp0"
python server.py
pause

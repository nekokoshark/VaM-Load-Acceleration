@echo off
setlocal
chcp 65001 >nul
"%~dp0VaMAcceleration\runtime\python\python.exe" -u "%~dp0VaMAcceleration\tools\installer.py" install --root "%~dp0." %*
set "result=%errorlevel%"
echo.
echo Exit status: %result%
if not "%VAMACC_NO_PAUSE%"=="1" pause
exit /b %result%

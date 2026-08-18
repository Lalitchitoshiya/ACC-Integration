@echo off
rem ============================================================
rem  ACC Water Connector - one-click start
rem  Starts the connector API in its own minimized window and
rem  opens the dashboard in your browser when it is ready.
rem  Keep the minimized "ACC Water Connector" window running;
rem  closing it stops the API (use stop-dashboard.bat to stop).
rem ============================================================

cd /d "%~dp0connector"

rem Already running? Just open the dashboard.
powershell -NoProfile -Command "try { Invoke-RestMethod http://localhost:5000/health -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
if %errorlevel%==0 (
  echo Connector already running - opening dashboard.
  start "" http://localhost:5000
  exit /b 0
)

echo Starting ACC Water Connector API...
start "ACC Water Connector" /min cmd /c "dotnet run --project src/Connector.Api --urls http://localhost:5000"

echo Waiting for the API to come up...
set tries=0
:waitloop
set /a tries+=1
timeout /t 2 /nobreak >nul
powershell -NoProfile -Command "try { Invoke-RestMethod http://localhost:5000/health -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
if %errorlevel%==0 goto ready
if %tries% lss 30 goto waitloop
echo.
echo ERROR: API did not start within 60 seconds - check the "ACC Water Connector" window for errors.
pause
exit /b 1

:ready
echo API is up - opening dashboard.
start "" http://localhost:5000
exit /b 0

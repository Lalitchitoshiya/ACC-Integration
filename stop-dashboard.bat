@echo off
rem Stops the ACC Water Connector API started by start-dashboard.bat.
taskkill /im Connector.Api.exe /f >nul 2>&1
if %errorlevel%==0 (echo Connector stopped.) else (echo Connector was not running.)
timeout /t 2 /nobreak >nul

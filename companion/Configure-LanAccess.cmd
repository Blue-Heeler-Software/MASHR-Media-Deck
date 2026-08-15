@echo off
setlocal

if /i "%~1"=="PairedPhone" goto validate
if /i "%~1"=="SameSubnet" goto validate
goto usage

:validate
if "%~2"=="" goto usage

fltmc >nul 2>&1
if errorlevel 1 (
  echo Run this file from an Administrator Command Prompt.
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Configure-LanAccess.ps1" -Scope "%~1" -PhoneAddress "%~2"
exit /b %errorlevel%

:usage
echo Usage:
echo   Configure-LanAccess.cmd PairedPhone PHONE_IP[,PHONE_IP...]
echo   Configure-LanAccess.cmd SameSubnet PHONE_IP
echo Examples:
echo   Configure-LanAccess.cmd PairedPhone 192.168.0.107,192.168.0.147
echo   Configure-LanAccess.cmd SameSubnet 192.168.0.107
exit /b 2

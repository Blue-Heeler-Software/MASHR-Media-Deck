@echo off
setlocal

if /i "%~1"=="PairedPhone" goto validate
if /i "%~1"=="SameSubnet" goto validate
goto usage

:validate
if "%~2"=="" goto usage
if "%~3"=="" goto usage
if /i "%~1"=="SameSubnet" if "%~4"=="" goto usage

fltmc >nul 2>&1
if errorlevel 1 (
  echo Run this file from an Administrator Command Prompt.
  exit /b 1
)

set "MODE=paired-phone"
set "REMOTE=%~2"
if /i "%~1"=="SameSubnet" (
  set "MODE=same-subnet"
  set "REMOTE=%~4"
)
set "PHONE=%~2"
set "PC=%~3"
set "EXE=%~dp0bin\Debug\net10.0-windows10.0.19041.0\MediaDeck.Companion.exe"

if not exist "%EXE%" (
  echo Build the MediaDeck companion first: %EXE%
  exit /b 1
)

schtasks /End /TN "MediaDeck Companion" >nul 2>&1
taskkill /IM MediaDeck.Companion.exe /F >nul 2>&1
netsh advfirewall firewall set rule name="mediadeck.companion.exe" new enable=no >nul
netsh advfirewall firewall delete rule name="MediaDeck Companion TCP (Restricted)" >nul 2>&1
netsh advfirewall firewall delete rule name="MediaDeck Discovery UDP (Restricted)" >nul 2>&1

netsh advfirewall firewall add rule name="MediaDeck Companion TCP (Restricted)" dir=in action=allow enable=yes profile=any program="%EXE%" protocol=TCP localport=43821 localip=%PC% remoteip=%REMOTE% interfacetype=lan >nul
if errorlevel 1 goto failed
netsh advfirewall firewall add rule name="MediaDeck Discovery UDP (Restricted)" dir=in action=allow enable=yes profile=any program="%EXE%" protocol=UDP localport=43822 localip=%PC% remoteip=%REMOTE% interfacetype=lan >nul
if errorlevel 1 goto failed

start "" /wait "%EXE%" --configure-lan %MODE% %PHONE%
if errorlevel 1 goto failed

schtasks /Run /TN "MediaDeck Companion" >nul 2>&1
if errorlevel 1 start "" "%EXE%"
echo MediaDeck %~1 mode is enabled: PC %PC%, remote %REMOTE%.
exit /b 0

:failed
echo MediaDeck LAN configuration failed. The old broad firewall rules remain disabled.
exit /b 1

:usage
echo Usage:
echo   Configure-LanAccess.cmd PairedPhone PHONE_IP PC_IP
echo   Configure-LanAccess.cmd SameSubnet PHONE_IP PC_IP NETWORK_CIDR
echo Examples:
echo   Configure-LanAccess.cmd PairedPhone 192.168.0.107 192.168.0.103
echo   Configure-LanAccess.cmd SameSubnet 192.168.0.107 192.168.0.103 192.168.0.0/24
exit /b 2

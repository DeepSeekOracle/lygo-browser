@echo off
setlocal EnableExtensions EnableDelayedExpansion
title LYGO BROWSER 1.0.0
color 0B

set "ROOT=<INSTALL_DIR>"
set "APP=%ROOT%\app"
set "EXE=%APP%\LygoBrowser.exe"
if not exist "%EXE%" set "EXE=%ROOT%\src\LygoBrowser\bin\Release\net9.0-windows\LygoBrowser.exe"
set "PY=C:\Python313\python.exe"
set "PORT=9337"
set "CHECK="
set "NOWAIT="
if /I "%~1"=="--check"  set "CHECK=1"
if /I "%~1"=="--nowait" set "NOWAIT=1"
if /I "%~1"=="--quiet"  set "CHECK=1" & set "NOWAIT=1"

echo.
echo    +==============================================================+
echo    ^|                                                              ^|
echo    ^|      L Y G O   B R O W S E R                                 ^|
echo    ^|      BOOKMARK BIBLE  .  AGENT + HUMAN                        ^|
echo    ^|                                                              ^|
echo    ^|      v1.0.0   bridge :9337   cdp :9333                       ^|
echo    +==============================================================+
echo.

if not exist "%EXE%" (
  echo    [FAIL] browser exe not found:
  echo           %EXE%
  echo    Build it:  dotnet publish "%ROOT%\src\LygoBrowser" -c Release -o "%APP%"
  if not defined CHECK pause
  exit /b 2
)

rem --- already running? ---
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
 "try { $r=Invoke-WebRequest 'http://127.0.0.1:%PORT%/api/health' -UseBasicParsing -TimeoutSec 2; if($r.StatusCode -eq 200){exit 0} } catch {}; exit 1" >nul 2>&1
if not errorlevel 1 (
  echo    [OK] already running  http://127.0.0.1:%PORT%/
  if not defined CHECK start "" "http://127.0.0.1:%PORT%/"
  if defined CHECK exit /b 0
  timeout /t 2 /nobreak >nul
  exit /b 0
)

echo    [BOOT] starting engine ^(WebView2 / CDP :9333^)...
start "" "%EXE%"
if defined NOWAIT (
  echo    [OK] launched
  exit /b 0
)

set /a W=0
:wait
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
 "try { $r=Invoke-WebRequest 'http://127.0.0.1:%PORT%/api/health' -UseBasicParsing -TimeoutSec 2; if($r.StatusCode -eq 200){exit 0} } catch {}; exit 1" >nul 2>&1
if not errorlevel 1 goto ready
set /a W+=1
if %W% GEQ 30 goto slow
timeout /t 1 /nobreak >nul
goto wait

:ready
echo.
echo    [OK] bridge online   http://127.0.0.1:%PORT%/
echo         bible          %ROOT%\data\bible\bookmarks.json
echo         agent token    %ROOT%\data\state\bridge.token
echo         agent cli      python "%ROOT%\tools\lygo_agent.py" status
echo.
if defined CHECK exit /b 0
timeout /t 2 /nobreak >nul
exit /b 0

:slow
echo.
echo    [WARN] engine did not answer on %PORT% within 30s.
echo           The window may still be loading. Check:
echo           %ROOT%\data\state\browser.log
if defined CHECK exit /b 1
pause
exit /b 1

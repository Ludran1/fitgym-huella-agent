@echo off
REM ============================================================
REM  Instala el agente como TAREA al iniciar sesion (corre en la
REM  sesion del usuario -> SI ve el lector USB; el servicio no, por Session 0).
REM  Reemplaza al servicio de Windows. Ejecutar como ADMINISTRADOR.
REM  HuellaAgent.exe + appsettings.json + run-hidden.vbs deben estar junto a este .bat.
REM ============================================================
setlocal

net session >nul 2>&1
if %errorLevel% neq 0 (
  echo  ERROR: ejecuta este script como ADMINISTRADOR.
  pause
  exit /b 1
)

set "EXE=%~dp0HuellaAgent.exe"
set "VBS=%~dp0run-hidden.vbs"
if not exist "%EXE%" ( echo  ERROR: falta HuellaAgent.exe junto al script. & pause & exit /b 1 )
if not exist "%VBS%" ( echo  ERROR: falta run-hidden.vbs junto al script. & pause & exit /b 1 )

echo  Cerrando instancias previas del agente (consola/servicio) para liberar el puerto 8000...
taskkill /im HuellaAgent.exe /f >nul 2>&1

echo  Quitando el servicio viejo (corria Mock por Session 0)...
sc stop HuellaAgent >nul 2>&1
sc delete HuellaAgent >nul 2>&1

echo  Limpiando datos viejos creados por LocalSystem (se recrean como usuario)...
rmdir /s /q "C:\ProgramData\HuellaAgent" >nul 2>&1

echo  Creando la tarea "FitGymHuellaAgent" (al iniciar sesion)...
schtasks /create /tn "FitGymHuellaAgent" /tr "wscript.exe \"%VBS%\"" /sc onlogon /f
if %errorLevel% neq 0 ( echo  ERROR creando la tarea. & pause & exit /b 1 )

echo  Arrancando el agente ahora...
start "" wscript.exe "%VBS%"

echo.
echo  Listo. El agente arranca solo al iniciar sesion, en tu sesion (ve el lector).
echo  Verifica:  http://localhost:8000/health  -^>  device: ZKTeco SLK20R
echo  (si dijera MockDevice, asegurate que el lector este enchufado y reintenta)
pause
endlocal

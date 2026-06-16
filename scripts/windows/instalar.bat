@echo off
REM ============================================================
REM  INSTALADOR TODO-EN-UNO del lector de huella (FitGym).
REM  Hace: driver del lector + agente + autostart (al iniciar sesion).
REM  El gym corre SOLO este archivo. Clic derecho -> Ejecutar como administrador.
REM ============================================================
setlocal

net session >nul 2>&1
if %errorLevel% neq 0 (
  echo.
  echo  ERROR: ejecuta este archivo como ADMINISTRADOR.
  echo  Clic derecho -^> "Ejecutar como administrador".
  echo.
  pause
  exit /b 1
)

set "EXE=%~dp0HuellaAgent.exe"
set "VBS=%~dp0run-hidden.vbs"
set "DRV=%~dp0setup-driver.exe"
if not exist "%EXE%" ( echo  ERROR: falta HuellaAgent.exe junto a este archivo. & pause & exit /b 1 )
if not exist "%VBS%" ( echo  ERROR: falta run-hidden.vbs junto a este archivo. & pause & exit /b 1 )

echo ==================================================
echo   Instalando el lector de huella - FitGym
echo ==================================================
echo.
echo  PASO 1/2 - Driver del lector
echo  Segui el asistente (Siguiente / Finalizar) y cerralo al terminar.
echo.
if exist "%DRV%" ( start /wait "" "%DRV%" ) else ( echo  (setup-driver.exe no encontrado, salteando el driver) )
echo.
echo  Enchufa el lector SLK20R a un USB si todavia no lo hiciste.
echo.
echo  PASO 2/2 - Agente + autostart
echo.
taskkill /im HuellaAgent.exe /f >nul 2>&1
sc stop HuellaAgent >nul 2>&1
sc delete HuellaAgent >nul 2>&1
rmdir /s /q "C:\ProgramData\HuellaAgent" >nul 2>&1
schtasks /create /tn "FitGymHuellaAgent" /tr "wscript.exe \"%VBS%\"" /sc onlogon /f
if %errorLevel% neq 0 ( echo  ERROR creando la tarea de autostart. & pause & exit /b 1 )
start "" wscript.exe "%VBS%"

echo.
echo  LISTO. El agente quedo corriendo y arranca solo al iniciar sesion.
echo.
echo  1. Verifica:  http://localhost:8000/health   (debe decir  device: ZKTeco SLK20R)
echo  2. En la app: Configuracion -^> Lector de huella -^> "Vincular lector".
echo.
pause
endlocal

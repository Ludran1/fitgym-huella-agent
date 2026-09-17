@echo off
REM ============================================================
REM  Agrega el VIGILANTE a una PC que YA tiene el agente instalado.
REM  Cada 1 minuto: arranca el agente si esta caido y lo reinicia si quedo con el
REM  lector simulado. NO toca el vinculo ni las huellas (instalar.bat si los borra).
REM
REM  Correrlo con la sesion de RECEPCION (la que usa el lector), con doble clic.
REM  vigilante.ps1 + vigilante-oculto.vbs deben estar junto a HuellaAgent.exe.
REM ============================================================
setlocal

set "EXE=%~dp0HuellaAgent.exe"
set "VBS=%~dp0vigilante-oculto.vbs"
if not exist "%EXE%" ( echo  ERROR: falta HuellaAgent.exe junto a este archivo. & pause & exit /b 1 )
if not exist "%VBS%" ( echo  ERROR: falta vigilante-oculto.vbs junto a este archivo. & pause & exit /b 1 )
if not exist "%~dp0vigilante.ps1" ( echo  ERROR: falta vigilante.ps1 junto a este archivo. & pause & exit /b 1 )

echo  Creando la tarea "FitGymHuellaVigilante" (cada 1 minuto, en esta sesion)...
schtasks /create /tn "FitGymHuellaVigilante" /tr "wscript.exe \"%VBS%\"" /sc minute /mo 1 /f
if %errorLevel% neq 0 ( echo  ERROR creando la tarea. & pause & exit /b 1 )

echo  Primera pasada ahora...
start "" wscript.exe "%VBS%"

echo.
echo  Listo. Lo que haga queda en C:\ProgramData\HuellaAgent\logs\vigilante.log
echo  Para quitarlo:  schtasks /delete /tn "FitGymHuellaVigilante" /f
pause
endlocal

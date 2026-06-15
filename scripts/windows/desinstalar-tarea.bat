@echo off
REM ============================================================
REM  Quita la tarea "FitGymHuellaAgent" y cierra el agente.
REM  Ejecutar como ADMINISTRADOR.
REM ============================================================
net session >nul 2>&1
if %errorLevel% neq 0 ( echo  Ejecuta como ADMINISTRADOR. & pause & exit /b 1 )

schtasks /delete /tn "FitGymHuellaAgent" /f
taskkill /im HuellaAgent.exe /f >nul 2>&1
echo.
echo  Tarea eliminada y agente cerrado.
pause

@echo off
REM ============================================================
REM  Quita el servicio HuellaAgent de Windows.
REM  Ejecutar como ADMINISTRADOR.
REM ============================================================
net session >nul 2>&1
if %errorLevel% neq 0 (
  echo  ERROR: ejecuta este script como ADMINISTRADOR.
  pause
  exit /b 1
)

echo  Deteniendo y eliminando el servicio HuellaAgent...
sc stop HuellaAgent
sc delete HuellaAgent
echo.
echo  Servicio HuellaAgent eliminado.
pause

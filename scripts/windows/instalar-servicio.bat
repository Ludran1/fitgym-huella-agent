@echo off
REM ============================================================
REM  Instala HuellaAgent como SERVICIO de Windows.
REM  - Arranca solo con Windows (start= auto).
REM  - Se reinicia solo si crashea.
REM  Ejecutar como ADMINISTRADOR (clic derecho -> Ejecutar como administrador).
REM  HuellaAgent.exe + appsettings.json deben estar JUNTO a este script.
REM ============================================================
setlocal

REM --- requiere admin ---
net session >nul 2>&1
if %errorLevel% neq 0 (
  echo.
  echo  ERROR: ejecuta este script como ADMINISTRADOR.
  echo  Clic derecho sobre el .bat -^> "Ejecutar como administrador".
  echo.
  pause
  exit /b 1
)

set "EXE=%~dp0HuellaAgent.exe"
if not exist "%EXE%" (
  echo  ERROR: no se encuentra HuellaAgent.exe junto a este script.
  pause
  exit /b 1
)

echo  Limpiando instalacion previa (si existe)...
sc stop HuellaAgent >nul 2>&1
sc delete HuellaAgent >nul 2>&1

echo  Creando servicio HuellaAgent...
sc create HuellaAgent binPath= "\"%EXE%\"" start= auto DisplayName= "FitGym Huella Agent"
if %errorLevel% neq 0 ( echo  ERROR al crear el servicio. & pause & exit /b 1 )

sc description HuellaAgent "Agente local del lector de huella ZKTeco SLK20R (FitGym)."

REM  Reinicio automatico: a los 5s, hasta 3 veces; resetea el contador a los 60s.
sc failure HuellaAgent reset= 60 actions= restart/5000/restart/5000/restart/5000

echo  Iniciando servicio...
sc start HuellaAgent

echo.
echo  Listo. El agente ahora arranca solo con Windows.
echo  Verifica en el navegador:  http://localhost:8000/health
echo.
pause
endlocal

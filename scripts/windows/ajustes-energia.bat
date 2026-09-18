@echo off
REM ============================================================
REM  Deja la PC lista para ser la puerta del gym:
REM   - el USB no se apaga por ahorro de energia (si no, el lector "desaparece")
REM   - la PC no se suspende ni hiberna estando enchufada
REM   - la pantalla puede apagarse: eso NO afecta al lector
REM  Ejecutar como ADMINISTRADOR. Solo toca el perfil de CORRIENTE.
REM ============================================================
setlocal

net session >nul 2>&1
if %errorLevel% neq 0 (
  echo  ERROR: clic derecho -^> "Ejecutar como administrador".
  pause
  exit /b 1
)

echo  1/4 Suspension del USB: apagada
powercfg /SETACVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0

echo  2/4 La PC no se suspende (en corriente)
powercfg /change standby-timeout-ac 0

echo  3/4 La PC no hiberna (en corriente)
powercfg /change hibernate-timeout-ac 0

echo  4/4 Aplicando el plan de energia
powercfg /SETACTIVE SCHEME_CURRENT

echo.
echo  LISTO.
echo.
echo  Falta UNA cosa que se hace a mano, y es importante:
echo   - Que Windows inicie sesion SOLO despues de un corte de luz.
echo     Menu Inicio -^> escribi  netplwiz  -^> Enter
echo     Destilda "Los usuarios deben escribir su nombre y contrasena"
echo     -^> Aceptar -^> escribi la contrasena del usuario dos veces.
echo.
echo   - Si es LAPTOP: Panel de control -^> Opciones de energia -^>
echo     "Elegir el comportamiento del cierre de la tapa" -^>
echo     Al cerrar la tapa (con corriente): NO HACER NADA.
echo.
pause
endlocal

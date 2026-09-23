@echo off
REM ============================================================
REM  INSTALADOR TODO-EN-UNO del lector de huella (FitGym).
REM  Driver + agente + autostart + vigilante + ajustes de energia + verificacion.
REM  El gym corre SOLO este archivo. Clic derecho -> Ejecutar como administrador.
REM
REM  POR QUE AHORA HACE TAMBIEN LOS AJUSTES DE ENERGIA:
REM  hasta el 23-sep terminaba diciendo "ahora ejecuta tambien ajustes-energia.bat".
REM  Ese "tambien", en una linea de texto al final y despues de haber dicho LISTO, no lo
REM  hacia nadie. Y el castigo no es inmediato: Windows apaga el USB por ahorro de energia
REM  recien cuando el lector pasa horas sin usarse, o sea que anda perfecto todo el primer
REM  dia y aparece muerto a la manana siguiente, sin que nadie ate una cosa con la otra.
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
echo  PASO 1/4 - Driver del lector
echo  Segui el asistente (Siguiente / Finalizar) y cerralo al terminar.
echo.
if exist "%DRV%" ( start /wait "" "%DRV%" ) else ( echo  (setup-driver.exe no encontrado, salteando el driver) )
echo.
echo  Enchufa el lector a un USB si todavia no lo hiciste.
echo  Sirve cualquier ZKTeco: SLK20R, ZK9500 y los de esa familia.
echo.

echo  PASO 2/4 - Agente y arranque automatico
taskkill /im HuellaAgent.exe /f >nul 2>&1
sc stop HuellaAgent >nul 2>&1
sc delete HuellaAgent >nul 2>&1
rmdir /s /q "C:\ProgramData\HuellaAgent" >nul 2>&1
schtasks /create /tn "FitGymHuellaAgent" /tr "wscript.exe \"%VBS%\"" /sc onlogon /f >nul
if %errorLevel% neq 0 ( echo  ERROR creando la tarea de arranque. & pause & exit /b 1 )
start "" wscript.exe "%VBS%"

REM Vigilante: arranca el agente si se cae y lo reinicia si quedo con el lector simulado.
if exist "%~dp0vigilante-oculto.vbs" (
  schtasks /create /tn "FitGymHuellaVigilante" /tr "wscript.exe \"%~dp0vigilante-oculto.vbs\"" /sc minute /mo 1 /f >nul
)
echo  OK.
echo.

echo  PASO 3/4 - Que Windows no apague el lector
REM El USB no se suspende por ahorro de energia: sin esto el lector "desaparece" solo
REM despues de unas horas sin usarse, y nadie ata el sintoma con la causa.
powercfg /SETACVALUEINDEX SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 >nul
REM La PC no se suspende ni hiberna estando enchufada. La pantalla si puede apagarse: eso
REM no afecta al lector.
powercfg /change standby-timeout-ac 0 >nul
powercfg /change hibernate-timeout-ac 0 >nul
powercfg /SETACTIVE SCHEME_CURRENT >nul
echo  OK.
echo.

echo  PASO 4/4 - Comprobando que quedo andando
REM Antes esto se le pedia a la persona: "abri http://localhost:8000/health y fijate si
REM device dice el modelo real". Ninguna recepcionista hace eso, y como el agente corre
REM oculto, una instalacion a medias se ve igual que una que anda.
REM
REM Va en un .ps1 aparte y no aca adentro: el quoting de batch rompe con cualquier texto
REM que traiga un espacio o un pipe, y el modelo del lector trae espacios. Costo un bug.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verificar.ps1"

echo.
echo  Dos cosas que hay que hacer A MANO, y son importantes:
echo.
echo   1. Que Windows inicie sesion SOLO despues de un corte de luz.
echo      Sin esto, si se corta la luz un domingo, el agente no arranca
echo      hasta que alguien se loguee: el gimnasio queda sin puerta.
echo      Menu Inicio -^> escribi  netplwiz  -^> Enter
echo      Destilda "Los usuarios deben escribir su nombre y contrasena"
echo      -^> Aceptar -^> escribi la contrasena del usuario dos veces.
echo.
echo   2. Si es LAPTOP: Panel de control -^> Opciones de energia -^>
echo      "Elegir el comportamiento del cierre de la tapa" -^>
echo      Al cerrar la tapa (con corriente): NO HACER NADA.
echo.
pause
endlocal

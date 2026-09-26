# ============================================================
#  vigilante.ps1 - mantiene vivo al agente. Lo corre una tarea cada 1 minuto
#  (instalar-vigilante.bat), oculto, en la sesion del usuario.
#
#  Por que existe: el agente v1.0.2 abre el lector UNA vez al arrancar y no reintenta.
#   - Si al iniciar sesion el USB todavia no estaba listo (zkfp2.Init fallo), queda con el
#     lector SIMULADO todo el dia y /health igual dice reader "connected". Paso en campo
#     el 08 y el 10-sep.
#   - Si el proceso se muere, nadie lo vuelve a levantar hasta el proximo inicio de sesion.
#
#  Que hace, en orden:
#   1. Si launch.ps1 esta corriendo (auto-update en curso), no toca nada.
#   2. Si /health no responde: arranca el agente (o lo reinicia si esta colgado).
#   3. Si responde con el lector simulado Y Windows ve un lector ZKTeco: lo reinicia.
#  Nunca reinicia mas de una vez cada 3 minutos. Deja rastro en logs\vigilante.log.
# ============================================================
param(
  [string]$Exe = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "HuellaAgent.exe"),
  [int]$Puerto = 8000
)
$ErrorActionPreference = "SilentlyContinue"

$datos   = Join-Path $env:ProgramData "HuellaAgent"
$logFile = Join-Path $datos "logs\vigilante.log"
$marca   = Join-Path $datos "vigilante.ultimo-reinicio"

function Log([string]$msg) {
  New-Item -ItemType Directory -Force -Path (Split-Path $logFile) | Out-Null
  Add-Content -Path $logFile -Value ("{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg)
}

function ReinicioReciente {
  if (-not (Test-Path $marca)) { return $false }
  return ((Get-Date) - (Get-Item $marca).LastWriteTime).TotalMinutes -lt 3
}

function Arrancar([string]$motivo) {
  if (ReinicioReciente) { Log "$motivo - pero ya reinicie hace menos de 3 min, espero"; return }
  Log $motivo
  Get-Process HuellaAgent | Stop-Process -Force
  Start-Sleep -Seconds 2
  if (-not (Test-Path $Exe)) { Log "no encuentro $Exe"; return }
  Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -WindowStyle Hidden
  New-Item -ItemType Directory -Force -Path $datos | Out-Null
  Set-Content -Path $marca -Value (Get-Date -Format o)
}

# 1. Auto-update en curso: launch.ps1 reemplaza el exe antes de lanzarlo.
$actualizando = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
  Where-Object { $_.CommandLine -match "launch\.ps1" }
if ($actualizando) { exit 0 }

# 2. Responde?
$health = $null
try { $health = Invoke-RestMethod -Uri "http://localhost:$Puerto/health" -TimeoutSec 3 } catch {}

$proc = Get-Process HuellaAgent | Select-Object -First 1
if (-not $health) {
  # Recien lanzado: abrir el lector tarda ~1-2 s y bajar las huellas de Supabase algo mas.
  if ($proc -and ((Get-Date) - $proc.StartTime).TotalSeconds -lt 90) { exit 0 }
  if ($proc) { Arrancar "no responde /health (pid $($proc.Id)); reinicio" }
  else       { Arrancar "agente caido; lo arranco" }
  exit 0
}

# 3. Responde, pero con el lector de verdad?
if ("$($health.device)" -match "Mock") {
  # ZKTeco = VID_1B55 (SLK20R, ZK9500). Si Windows no ve el USB, reiniciar no arregla nada.
  $usb = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match "VID_1B55" -and $_.Status -eq "OK" }
  if (-not $usb) { exit 0 }
  Arrancar "lector simulado con el USB presente ($($usb[0].FriendlyName)); reinicio"
  exit 0
}

# 4. Chequeo de actualizacion, una vez por dia.
#
# POR QUE ESTA ACA Y NO SOLO EN LA TAREA DE INICIO DE SESION. La auto-actualizacion vive
# en launch.ps1, y a launch.ps1 lo corria UNICAMENTE la tarea `onlogon`. O sea que una PC
# que queda prendida con la sesion abierta —que es exactamente lo que se le pide a la PC de
# recepcion: 30 dias sin que nadie la toque— no se actualizaba NUNCA. Y si a esa PC ademas
# le falta la tarea de arranque, el vigilante la mantenia viva para siempre en una version
# vieja, en silencio. La promesa "se actualiza solo" no se cumplia justo en el caso que
# mas importa.
#
# Solo se llega aca con el agente sano (los pasos 2 y 3 ya salieron). launch.ps1 sabe no
# lanzar un segundo proceso si este sigue vivo, y solo lo reemplaza si hay version nueva:
# el chequeo normal son unos KB contra la API de GitHub, no los 55 MB del ZIP.
$marcaUpd = Join-Path $datos "vigilante.ultimo-chequeo"
$tocaChequear = $true
if (Test-Path $marcaUpd) {
  $tocaChequear = ((Get-Date) - (Get-Item $marcaUpd).LastWriteTime).TotalHours -ge 20
}
if ($tocaChequear) {
  $launch = Join-Path (Split-Path $Exe) "launch.ps1"
  if (Test-Path $launch) {
    New-Item -ItemType Directory -Force -Path $datos | Out-Null
    Set-Content -Path $marcaUpd -Value (Get-Date -Format o)   # antes de correr: si falla, no se reintenta en loop
    Log "chequeo diario de actualizacion"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launch
  } else {
    Log "no encuentro launch.ps1: esta PC NO se va a actualizar sola"
  }
}

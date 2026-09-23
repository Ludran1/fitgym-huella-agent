# ============================================================
#  verificar.ps1 — le dice a la persona que instalo si quedo andando.
#
#  POR QUE EXISTE. Hasta el 23-sep el instructivo terminaba pidiendo:
#  "abri http://localhost:8000/health y fijate si device dice el modelo real; si dice
#  (sin lector), mira last_error". Ninguna recepcionista hace eso. Y como el agente corre
#  oculto, una instalacion a medias se ve exactamente igual que una que anda.
#
#  Lo corre instalar.bat al final. Vive aparte y no adentro del .bat a proposito: el
#  quoting de batch rompe con cualquier texto que traiga un espacio o un caracter especial
#  —el modelo del lector trae las dos cosas— y eso ya costo un bug.
# ============================================================
[CmdletBinding()]
param(
  [string]$Url = "http://localhost:8000/health",
  [int]$EsperaSeg = 45
)

function Linea($t, $color = "Gray") { Write-Host "  $t" -ForegroundColor $color }

$hasta = (Get-Date).AddSeconds($EsperaSeg)
$h = $null
while ((Get-Date) -lt $hasta -and -not $h) {
  try { $h = Invoke-RestMethod -Uri $Url -TimeoutSec 3 } catch { Start-Sleep -Seconds 2 }
}

Write-Host ""
Write-Host "==================================================" -ForegroundColor DarkGray

if (-not $h) {
  Write-Host "  EL AGENTE NO ARRANCO" -ForegroundColor Red
  Write-Host ""
  Linea "Reinicia la PC y volve a correr el instalador."
  Linea "Si sigue igual, mandanos esto:  http://localhost:8000/logs"
  Write-Host "==================================================" -ForegroundColor DarkGray
  exit 2
}

# El orden de las preguntas importa: un lector SIMULADO reporta "connected", asi que si se
# pregunta primero por reader se da por buena una instalacion que no lee ningun dedo. Ese
# fue el bug de campo del 08 y el 10-sep.
if ($h.device -like '*Mock*') {
  Write-Host "  EL AGENTE ARRANCO, PERO SIN LECTOR DE VERDAD" -ForegroundColor Yellow
  Write-Host ""
  Linea "Casi siempre es el driver. Volve a correr el instalador y hace el PASO 1."
  Linea "Tambien puede ser que otro programa tenga tomado el lector."
  Write-Host "==================================================" -ForegroundColor DarkGray
  exit 3
}

if ($h.reader -ne 'connected') {
  Write-Host "  EL AGENTE ANDA PERO NO VE EL LECTOR" -ForegroundColor Yellow
  Write-Host ""
  if ($h.last_error) { Linea "Motivo: $($h.last_error)" }
  Linea "Revisa que este enchufado y proba otro puerto USB."
  Write-Host "==================================================" -ForegroundColor DarkGray
  exit 4
}

Write-Host "  LISTO. El lector esta andando." -ForegroundColor Green
Write-Host ""
Linea "Lector:  $($h.device)"
if ($h.version) { Linea "Agente:  v$($h.version)" }
Write-Host ""
if ($h.durable -eq $true -and $h.gym) {
  Linea "Ya esta vinculado a: $($h.gym)" "Green"
  Linea "$($h.templates_loaded) huellas cargadas."
} else {
  Write-Host "  Falta UN paso, en la app:" -ForegroundColor Cyan
  Linea "Configuracion -> Lector de huella -> boton Vincular"
  Linea "Ahi se bajan las huellas del gimnasio y su configuracion."
}
Write-Host "==================================================" -ForegroundColor DarkGray
exit 0

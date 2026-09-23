# ============================================================
#  launch.ps1 — arranca HuellaAgent y lo AUTO-ACTUALIZA.
#  Lo corre la tarea al iniciar sesion (via run-hidden.vbs, oculto).
#
#  EL FRENO DE MANO, que NO esta en este script: /releases/latest de GitHub ignora los
#  pre-releases y los drafts. O sea que publicar una version como PRE-RELEASE hace que
#  ningun gimnasio la tome. Se instala a mano en una PC, se prueba, y recien cuando anda se
#  le saca la marca de pre-release: ahi pasa a ser "latest" y la toman todos.
#  Publicar deja de ser lo mismo que desplegar, sin escribir una linea. Ver README.
#
#  LO QUE SI ESTA ACA: que la PC se repare sola. Antes se reemplazaba el exe y se lo
#  lanzaba sin mirar si arrancaba. Si una version no abria —una DLL que falta, un
#  appsettings roto, el SDK cambiado— esa PC se quedaba con un agente muerto, en silencio,
#  hasta que alguien fuera. Y como las 14 se actualizan solas al iniciar sesion, era el
#  mismo dia para todas.
# ============================================================
$ErrorActionPreference = "Stop"
$dir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe   = Join-Path $dir "HuellaAgent.exe"
$vfile = Join-Path $dir "version.txt"
$repo  = "Ludran1/fitgym-huella-agent-dist"

# Cuanto se le da al agente nuevo para contestar antes de dar la actualizacion por fallida.
# Un arranque normal tarda un par de segundos; 30 es para una PC lenta con antivirus.
$EsperaArranqueSeg = 30

$logDir = Join-Path $env:ProgramData "HuellaAgent\logs"
function Anotar($msg) {
  try {
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    Add-Content -Path (Join-Path $logDir "launcher.log") -Value "$(Get-Date -Format s)  $msg"
  } catch { }
}

function Get-LocalVersion {
  if (Test-Path $vfile) { try { return [version]((Get-Content $vfile -Raw).Trim()) } catch {} }
  if (Test-Path $exe)   { try { return [version]((Get-Item $exe).VersionInfo.FileVersion) } catch {} }
  return [version]"0.0.0"
}

# ¿Contesta el agente, y es LA VERSION QUE ACABAMOS DE INSTALAR?
#
# Preguntar solo "¿contesta /health?" no alcanza: si el exe nuevo no abre porque el puerto
# 8000 lo tiene tomado una instancia vieja, /health contesta igual —la vieja— y la
# verificacion pasaria estando todo mal. La version es lo unico que distingue.
function Arranco($esperada) {
  $hasta = (Get-Date).AddSeconds($EsperaArranqueSeg)
  while ((Get-Date) -lt $hasta) {
    try {
      $h = Invoke-RestMethod -Uri "http://localhost:8000/health" -TimeoutSec 3
      if ($h.version -and ([version]$h.version) -eq $esperada) { return $true }
    } catch { }
    Start-Sleep -Seconds 2
  }
  return $false
}

$actualizadoA = $null
$backup = "$exe.bak"

# ── 1. Auto-update (best-effort; nunca tira el arranque) ──────────────────────
try {
  # /releases/latest NO devuelve pre-releases ni drafts: ese es el freno.
  $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" `
           -Headers @{ "User-Agent" = "HuellaAgent" } -TimeoutSec 10
  $latest = [version]($rel.tag_name -replace '^v','')
  $local  = Get-LocalVersion

  if ($latest -gt $local) {
    $asset = $rel.assets | Where-Object { $_.name -eq "HuellaAgent-Setup.zip" } | Select-Object -First 1
    if ($asset) {
      $tmpZip = Join-Path $env:TEMP "HuellaAgent-update.zip"
      $tmpDir = Join-Path $env:TEMP "HuellaAgent-update"
      Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmpZip -TimeoutSec 600
      if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
      Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force

      $newExe = Join-Path $tmpDir "HuellaAgent.exe"
      $newVer = Join-Path $tmpDir "version.txt"
      if (Test-Path $newExe) {
        # El agente viejo tiene que soltar el puerto ANTES de copiar encima: si no, el
        # nuevo no puede abrir el 8000 y se apaga solo a los pocos segundos.
        Get-Process HuellaAgent -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 800

        if (Test-Path $exe) { Copy-Item $exe $backup -Force }   # el que vamos a restaurar si falla
        Copy-Item $newExe $exe -Force
        if (Test-Path $newVer) { Copy-Item $newVer $vfile -Force }
        else { Set-Content -Path $vfile -Value $latest.ToString() }

        # Los SCRIPTS tambien, no solo el exe.
        #
        # Hasta la v1.2.0 esto copiaba el exe y nada mas, asi que cualquier mejora en el
        # instalador, en el vigilante o en este mismo launcher exigia ir hasta la PC del
        # gimnasio. Con catorce gimnasios eso no escala: el auto-update servia para el
        # programa y no para todo lo demas que lo rodea.
        #
        # Se copia TODO menos dos cosas:
        #   · appsettings.json  -> es la configuracion de ESE gimnasio. Pisarla seria
        #                          borrarle el puerto del rele o el torniquete a quien lo
        #                          tenga configurado a mano.
        #   · setup-driver.exe  -> 13 MB que ya estan instalados. No hace falta moverlos.
        #
        # Sobrescribir launch.ps1 mientras corre es seguro: PowerShell ya lo leyo entero a
        # memoria. La version nueva empieza a valer en el proximo inicio de sesion.
        $refrescados = 0
        Get-ChildItem $tmpDir -File | Where-Object {
          $_.Name -notin @("HuellaAgent.exe", "version.txt", "appsettings.json", "setup-driver.exe")
        } | ForEach-Object {
          try { Copy-Item $_.FullName (Join-Path $dir $_.Name) -Force; $refrescados++ } catch { }
        }

        $actualizadoA = $latest
        Anotar "actualizado de $local a $latest ($refrescados scripts refrescados)"
      }
      Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
      Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
} catch {
  # sin internet / GitHub caido / rate-limit → seguir con lo que hay
  Anotar "no se pudo chequear la actualizacion: $($_.Exception.Message)"
}

# ── 2. Lanzar el agente OCULTO ────────────────────────────────────────────────
Start-Process -FilePath $exe -WindowStyle Hidden

# ── 3. Si actualizamos, comprobar que de verdad arranco ───────────────────────
#
# Y si no, volver a la version anterior SOLO. Es lo unico que evita que una version mala
# deje una PC —o las catorce— con un agente muerto hasta que alguien vaya. El .bak ya se
# guardaba desde antes; lo que faltaba era usarlo.
if ($actualizadoA -and (Test-Path $backup)) {
  if (Arranco $actualizadoA) {
    Anotar "la version $actualizadoA arranco bien"
  } else {
    Anotar "LA VERSION $actualizadoA NO ARRANCO: volviendo a la anterior"
    try {
      Get-Process HuellaAgent -ErrorAction SilentlyContinue | Stop-Process -Force
      Start-Sleep -Milliseconds 800
      Copy-Item $backup $exe -Force
      # version.txt vuelve a lo que dice el exe restaurado: si quedara la version nueva,
      # el proximo arranque creeria que ya esta al dia y no reintentaria nunca. Pero
      # tampoco queremos que reintente en loop, asi que ver la nota de abajo.
      $v = (Get-Item $exe).VersionInfo.FileVersion
      if ($v) { Set-Content -Path $vfile -Value ([version]$v).ToString() }
      Start-Process -FilePath $exe -WindowStyle Hidden
      Anotar "restaurada la version anterior ($v)"
    } catch {
      Anotar "NO SE PUDO RESTAURAR: $($_.Exception.Message)"
    }
  }
}

# NOTA sobre el reintento: al volver atras, el proximo inicio de sesion va a ver de nuevo
# que hay una version mas nueva y la va a intentar otra vez. Es a proposito — si el
# problema era del momento (disco lleno, antivirus), se arregla solo. Si la version esta
# rota de verdad, hay que sacarla de "latest" en GitHub, que es de donde salen todas: dos
# clics, y las PCs dejan de intentarlo. Por eso el freno vive alla y no aca.

# ============================================================================
#  publicar.ps1 — arma lo que se publica de cada version del agente:
#
#    · PeakGym-Lector-Setup.exe  el INSTALADOR. Es lo que baja un gimnasio desde el
#                                panel. Doble clic, Siguiente, Instalar. Ver
#                                installer/PeakGymLector.iss.
#    · HuellaAgent-Setup.zip     la ACTUALIZACION. Nadie lo abre a mano: lo baja
#                                launch.ps1 de cada PC ya instalada y copia el exe y
#                                los scripts. Va plano (sin carpetas) porque asi lo
#                                espera el launch.ps1 de TODAS las versiones ya
#                                instaladas, y sin el driver (ya esta instalado).
#
#  POR QUE EXISTE
#  --------------
#  Hasta el 23-sep el ZIP se armaba a mano: compilar, acordarse de editar
#  appsettings.json para poner UseRealDevice en true, copiar los scripts, escribir
#  version.txt, meter el driver y comprimir. Seis pasos de memoria, sin nadie que
#  revisara el resultado.
#
#  Los dos que duelen si salen mal:
#
#    · appsettings.json con UseRealDevice en false → el agente arranca con MockDevice
#      en TODOS los gimnasios que instalen ese ZIP. No grita: queda vivo, /health
#      contesta, y solo "device" delata que no hay lector de verdad.
#    · version.txt mal o ausente → launch.ps1 compara version.txt contra el tag del
#      ultimo release para auto-actualizar, y su try/catch se traga cualquier error.
#      Los gimnasios se quedan en la version vieja PARA SIEMPRE y nadie se entera.
#
#  Por eso este script no solo arma el paquete: lo VERIFICA antes de comprimirlo, y
#  se niega a generar un ZIP que no vaya a andar.
#
#  USO
#  ---
#    pwsh scripts/publicar.ps1 -Driver C:\ruta\setup-driver.exe
#    pwsh scripts/publicar.ps1 -SinDriver          # para probar el empaquetado
#
#  Necesita Inno Setup 6 (winget install JRSoftware.InnoSetup).
# ============================================================================
[CmdletBinding()]
param(
  # El instalador del SDK/driver ZKFinger. No esta en el repo (es un binario de ZKTeco).
  [string]$Driver = "",
  # Permite un ZIP SIN driver, a proposito. Sirve para probar el script o para una PC
  # donde el driver ya esta. Nunca es el default: un ZIP sin driver instala un agente
  # que no abre el lector, y el gimnasio no tiene como saberlo.
  [switch]$SinDriver,
  [string]$Salida = "publish",
  # El compilador de Inno Setup. Vacio = se busca donde lo deja winget o el instalador.
  [string]$Iscc = ""
)

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $raiz

function Paso($t) { Write-Host "`n>> $t" -ForegroundColor Cyan }
function Mal($t)  { Write-Host "   ERROR: $t" -ForegroundColor Red }

# ── 1. La version sale del csproj, no de un argumento ────────────────────────
# Un argumento se puede equivocar; el csproj es el que termina en el FileVersion del
# exe, asi que es la unica fuente que no puede desincronizarse de lo que se compilo.
$csproj  = Join-Path $raiz "src/HuellaAgent/HuellaAgent.csproj"
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ }
if (-not $version) { Mal "no encontre <Version> en $csproj"; exit 1 }
$version = "$version".Trim()
Paso "Version $version (de HuellaAgent.csproj)"

# ── 2. Compilar ──────────────────────────────────────────────────────────────
# self-contained: la PC del gimnasio NO tiene .NET y no le vamos a pedir que lo
# instale — seria otro paso manual, justo lo que estamos sacando.
# single-file: launch.ps1 se auto-actualiza reemplazando UN archivo, HuellaAgent.exe.
# Si el publish quedara repartido en cien DLLs, el updater dejaria la instalacion a
# medias: exe nuevo con dependencias viejas.
$tmp = Join-Path $raiz "$Salida/_app"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Paso "Compilando win-x64 (self-contained, un solo archivo)"
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $tmp | Out-String | Write-Verbose
if ($LASTEXITCODE -ne 0) { Mal "fallo el dotnet publish"; exit 1 }

# ── 3. Armar el paquete ──────────────────────────────────────────────────────
$pkg = Join-Path $raiz "$Salida/HuellaAgent-Setup"
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Path $pkg | Out-Null

Paso "Armando el paquete"
Copy-Item (Join-Path $tmp "HuellaAgent.exe") $pkg
Copy-Item (Join-Path $raiz "scripts/windows/*") $pkg -Recurse

# La config de produccion NO se edita a mano: se copia de deploy/, que se revisa en un
# diff como cualquier otro archivo. Ver deploy/LEEME.md.
Copy-Item (Join-Path $raiz "deploy/appsettings.produccion.json") (Join-Path $pkg "appsettings.json")

# version.txt: lo lee launch.ps1 para decidir si hay que actualizar.
Set-Content -Path (Join-Path $pkg "version.txt") -Value $version -NoNewline

if ($Driver) {
  if (-not (Test-Path $Driver)) { Mal "no existe el driver: $Driver"; exit 1 }
  Copy-Item $Driver (Join-Path $pkg "setup-driver.exe")
} elseif (-not $SinDriver) {
  Mal "falta -Driver <ruta al setup del SDK ZKFinger>."
  Write-Host "   Sin driver, el agente se instala pero NO abre el lector, y el gimnasio" -ForegroundColor Yellow
  Write-Host "   no tiene como darse cuenta. Si es a proposito, pasa -SinDriver." -ForegroundColor Yellow
  exit 1
}

# ── 4. Verificar ANTES de comprimir ──────────────────────────────────────────
# Es el punto del script. Un ZIP que no anda se descubre en la PC de un gimnasio,
# despues de bajar 58 MB, y sin nadie que sepa leer un log.
Paso "Verificando el paquete"
$errores = @()

# El instalador (PeakGymLector.iss) no compila si le falta alguno de estos.
foreach ($f in @("HuellaAgent.exe", "launch.ps1", "run-hidden.vbs", "vigilante.ps1", "vigilante-oculto.vbs",
                 "verificar.ps1", "appsettings.json", "version.txt", "LEEME.txt")) {
  if (-not (Test-Path (Join-Path $pkg $f))) { $errores += "falta $f" }
}

$cfg = Get-Content (Join-Path $pkg "appsettings.json") -Raw | ConvertFrom-Json
if ($cfg.Agent.UseRealDevice -ne $true) {
  $errores += "appsettings.json tiene UseRealDevice=$($cfg.Agent.UseRealDevice): el agente arrancaria SIMULADO en todos los gimnasios"
}

$verTxt = (Get-Content (Join-Path $pkg "version.txt") -Raw).Trim()
if ($verTxt -ne $version) { $errores += "version.txt dice '$verTxt' y el csproj dice '$version'" }

$exeVer = (Get-Item (Join-Path $pkg "HuellaAgent.exe")).VersionInfo.FileVersion
if ($exeVer -and -not $exeVer.StartsWith($version)) {
  $errores += "el exe dice $exeVer y el csproj dice $version (¿compilo una version vieja?)"
}

if ($errores.Count) {
  Write-Host ""
  Mal "el paquete NO esta bien, no lo comprimo:"
  $errores | ForEach-Object { Write-Host "   · $_" -ForegroundColor Red }
  exit 1
}

# ── 5. El instalador ─────────────────────────────────────────────────────────
if ($SinDriver) {
  # Un instalador sin driver instalaria un agente que no abre el lector: no se arma.
  Write-Host "   (sin driver: no armo el instalador .exe, solo el ZIP)" -ForegroundColor Yellow
} else {
  Paso "Compilando el instalador (Inno Setup)"
  if (-not $Iscc) {
    $Iscc = @(
      (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
      (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
      (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  }
  if (-not $Iscc -or -not (Test-Path $Iscc)) {
    Mal "no encuentro ISCC.exe. Instala Inno Setup 6:  winget install JRSoftware.InnoSetup"
    exit 1
  }
  $iss = Join-Path $raiz "installer/PeakGymLector.iss"
  & $Iscc "/DAppVersion=$version" "/DPkgDir=$pkg" "/O$(Join-Path $raiz $Salida)" "/Q" $iss
  if ($LASTEXITCODE -ne 0) { Mal "fallo la compilacion del instalador"; exit 1 }
  $exeSetup = Join-Path $raiz "$Salida/PeakGym-Lector-Setup.exe"
  $exeVerSetup = (Get-Item $exeSetup).VersionInfo.ProductVersion
  if (-not "$exeVerSetup".StartsWith($version)) { Mal "el instalador dice $exeVerSetup y el csproj $version"; exit 1 }
  # El mismo instalador con la versión en el nombre (pedido de Adriano, 09-oct): bajaba
  # como "PeakGym-Lector-Setup (1).exe" y no había forma de saber si era uno nuevo. El
  # panel enlaza éste (ver ultimaVersionAgente en el SPA). El de nombre fijo se sigue
  # subiendo porque /releases/latest/download/PeakGym-Lector-Setup.exe es el link de
  # respaldo y el que quedó en documentos y mensajes viejos.
  $exeConVersion = Join-Path $raiz "$Salida/PeakGym-Lector-Setup-$version.exe"
  Copy-Item $exeSetup $exeConVersion -Force
}

# ── 6. El ZIP de actualizacion ──────────────────────────────────────────────
# Sin el driver: launch.ps1 nunca lo usa (ya esta instalado) y son 13 MB que cada PC
# bajaria en cada actualizacion.
$zip = Join-Path $raiz "$Salida/HuellaAgent-Setup.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Get-ChildItem $pkg -File | Where-Object { $_.Name -ne "setup-driver.exe" } |
  Compress-Archive -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ""
Write-Host "LISTO  v$version" -ForegroundColor Green
if (-not $SinDriver) {
  $mbExe = [math]::Round((Get-Item $exeSetup).Length / 1MB, 1)
  Write-Host "  instalador     $exeSetup  ($mbExe MB)" -ForegroundColor Green
  Write-Host "  con versión    $exeConVersion" -ForegroundColor Green
}
Write-Host "  actualizacion  $zip  ($mb MB)" -ForegroundColor Green
if ($SinDriver) { Write-Host "OJO: va SIN driver. No sirve para un gimnasio." -ForegroundColor Yellow }
Write-Host ""
Write-Host "Falta publicarlo en Ludran1/fitgym-huella-agent-dist, con LOS TRES archivos (los dos instaladores y el ZIP):" -ForegroundColor Gray
Write-Host "  1. Tag v$version   <- si no coincide, launch.ps1 no actualiza a nadie." -ForegroundColor Gray
Write-Host "  2. Marcalo como PRE-RELEASE." -ForegroundColor Yellow
Write-Host "     /releases/latest ignora los pre-releases: ningun gimnasio lo va a tomar." -ForegroundColor Gray
Write-Host "  3. Instalalo A MANO en una PC y probalo con el lector." -ForegroundColor Gray
Write-Host "  4. Recien ahi sacale la marca: pasa a ser latest y lo toman todos." -ForegroundColor Gray

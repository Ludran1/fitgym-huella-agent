# ============================================================================
#  publicar.ps1 — arma HuellaAgent-Setup.zip, el ZIP que instala un gimnasio.
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
# ============================================================================
[CmdletBinding()]
param(
  # El instalador del SDK/driver ZKFinger. No esta en el repo (es un binario de ZKTeco).
  [string]$Driver = "",
  # Permite un ZIP SIN driver, a proposito. Sirve para probar el script o para una PC
  # donde el driver ya esta. Nunca es el default: un ZIP sin driver instala un agente
  # que no abre el lector, y el gimnasio no tiene como saberlo.
  [switch]$SinDriver,
  [string]$Salida = "publish"
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

# instalar.bat aborta si le falta alguno de estos.
foreach ($f in @("HuellaAgent.exe", "instalar.bat", "run-hidden.vbs", "vigilante-oculto.vbs",
                 "ajustes-energia.bat", "appsettings.json", "version.txt", "LEEME.txt")) {
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

# ── 5. Comprimir ─────────────────────────────────────────────────────────────
$zip = Join-Path $raiz "$Salida/HuellaAgent-Setup.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$pkg/*" -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ""
Write-Host "LISTO  $zip  ($mb MB, v$version)" -ForegroundColor Green
if ($SinDriver) { Write-Host "OJO: este ZIP va SIN driver. No sirve para un gimnasio." -ForegroundColor Yellow }
Write-Host ""
Write-Host "Falta publicarlo como release v$version en Ludran1/fitgym-huella-agent-dist:" -ForegroundColor Gray
Write-Host "  el tag TIENE que ser v$version o launch.ps1 no va a actualizar a nadie." -ForegroundColor Gray

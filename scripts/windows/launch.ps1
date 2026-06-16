# ============================================================
#  launch.ps1 — arranca HuellaAgent y lo AUTO-ACTUALIZA.
#  Lo corre la tarea al iniciar sesion (via run-hidden.vbs, oculto).
#  Al arranque (cuando NO hay agente corriendo) chequea el ultimo release en GitHub;
#  si hay una version mayor, baja el ZIP, reemplaza el exe + version.txt (guarda .bak)
#  y lanza el agente. Si no hay internet / GitHub falla, sigue con la version actual.
# ============================================================
$ErrorActionPreference = "Stop"
$dir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe   = Join-Path $dir "HuellaAgent.exe"
$vfile = Join-Path $dir "version.txt"
$repo  = "Ludran1/fitgym-huella-agent-dist"

function Get-LocalVersion {
  if (Test-Path $vfile) { try { return [version]((Get-Content $vfile -Raw).Trim()) } catch {} }
  if (Test-Path $exe)   { try { return [version]((Get-Item $exe).VersionInfo.FileVersion) } catch {} }
  return [version]"0.0.0"
}

# ── 1. Auto-update (best-effort; nunca tira el arranque) ──────────────────────
try {
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
        if (Test-Path $exe) { Copy-Item $exe "$exe.bak" -Force }   # backup para rollback manual
        Copy-Item $newExe $exe -Force
        if (Test-Path $newVer) { Copy-Item $newVer $vfile -Force }
        else { Set-Content -Path $vfile -Value $latest.ToString() }
      }
      Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
      Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
} catch {
  # sin internet / GitHub caido / rate-limit → seguir con lo que hay
}

# ── 2. Lanzar el agente OCULTO ────────────────────────────────────────────────
Start-Process -FilePath $exe -WindowStyle Hidden

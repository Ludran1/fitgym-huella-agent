# huella-agent

Agente local del lector de huella **ZKTeco SLK20R** para FitGym. Corre en la PC de
recepción (Windows en producción), expone una API HTTP en `http://localhost:8000` y la
consume el navegador del kiosko (`src/lib/huellaApi.ts`). Ver PRD vault doc 39.

## Por qué local

El SLK20R es **USB** → lo lee un proceso en la misma PC donde está enchufado, no un
server remoto. El agente **nunca** usa la service-role de Supabase: para la prueba
reader-first guarda los templates en un **archivo local**; el marcado de asistencia lo
hace el kiosko con `kiosk_marcar_asistencia` (que ya existe).

## Contrato HTTP (lockeado por huellaApi.ts)

| Endpoint | Body | Respuesta |
|---|---|---|
| `GET /health` | — | `{ok, reader, device, templates_loaded}` |
| `POST /api/fingerprint/capture` | `{timeout}` | `{template, quality}` · 408 sin dedo |
| `POST /api/fingerprint/enroll` | `{cliente_id, tenant_id, template1..3}` | `{ok, uid}` |
| `POST /api/fingerprint/identify` | `{tenant_id}` | 200 `{ok, cliente_id, score}` · 404 sin match · 408 sin dedo |
| `POST /api/turnstile/open` | `{tenant_id}` | `{ok}` · Fase 7 (torniquete, opcional) |

`x-api-key` opcional (si se setea `Agent:ApiKey`). CORS abierto (es localhost).

## Desarrollo (Linux/CI, sin hardware)

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
dotnet test                                   # ContractTests con FakeDevice
dotnet run --project src/HuellaAgent          # levanta el agente con MockDevice en :8000
```

`MockDevice` simula capturas y "reconoce" al último enrolado → permite probar el flujo
entero (enroll → identify → marcar asistencia) sin SDK ni lector.

## Producción (Windows, con SDK + lector) — Fase 6

Integra el **ZKFinger SDK 5.3.0.33** vía el wrapper oficial `libzkfpcsharp` (clase
`zkfp2`), igual que el demo del SDK (`C#/Demo2/Form1.cs`). El wrapper x64 está vendoreado
en `src/HuellaAgent/sdk/win-x64/libzkfpcsharp.dll` y solo se referencia al publicar para
win-x64 (define `ZKFP`); en Linux/CI no se compila → build/test cross-platform.

**Build del .exe (se puede hacer DESDE Linux):**
```bash
dotnet publish src/HuellaAgent -r win-x64 --self-contained -c Release -p:PublishSingleFile=true
```
El wrapper queda embebido en `HuellaAgent.exe`. (Ya verificado que compila.)

**En la PC Windows:**
1. Correr el **`setup.exe`** del SDK → instala el **driver USB** + el `libzkfp.dll` **nativo**
   (que `libzkfpcsharp` P/Invoca en runtime). Enchufar el SLK20R.
2. (Opcional pero recomendado) Correr el **Demo del SDK** primero para confirmar que el
   lector captura, aislado de este agente.
3. Copiar `HuellaAgent.exe` + `appsettings.json` con `Agent:UseRealDevice = true`.
4. Correr el exe → `/health` debe dar `reader: connected`.
5. 🔴 **R1**: calibrar `Agent:IdentifyThreshold` (FAR/FRR) con dedos reales antes de piloto
   (admitir al cliente equivocado es el peor fallo del producto).

## Autostart en la PC de recepción

El agente debe **arrancar solo** (no depender de que alguien abra el exe). Hay 2 formas; en
campo (2026-06-15) se confirmó cuál sirve para el **lector USB**:

### ✅ Tarea al iniciar sesión (USAR ESTA) — `instalar-tarea.bat`
```
scripts/windows/instalar-tarea.bat     (clic derecho -> Ejecutar como administrador)
scripts/windows/desinstalar-tarea.bat
scripts/windows/run-hidden.vbs         (lanza el exe sin ventana; lo usa la tarea)
```
Crea una tarea programada `FitGymHuellaAgent` con trigger **onlogon** que corre el agente en
la **sesión interactiva del usuario** (oculto, vía `run-hidden.vbs`). Ahí el lector USB **sí**
se ve. Quita el servicio viejo y limpia `%ProgramData%\HuellaAgent` (lo recrea como usuario).

### ❌ Servicio de Windows — `instalar-servicio.bat` (NO sirve con el SLK20R)
`Program.cs` soporta `UseWindowsService()`, pero como **LocalSystem (Session 0)** el SDK
ZKFinger **no accede al USB** → `ZkfpDevice` falla y cae a `MockDevice` (el `/health` dice
`device: "MockDevice (SLK20R simulado)"` aunque `reader: connected`). Confirmado en campo: a
mano (consola, sesión de usuario) da `ZKTeco SLK20R`; como servicio da Mock. Por eso se usa la
**tarea al logon**, no el servicio. Los scripts del servicio quedan por si algún hardware ZK sí
funciona en Session 0, pero el SLK20R no.

⚠️ Verificar tras instalar la tarea: `http://localhost:8000/health` → debe decir
`device: "ZKTeco SLK20R"` (NO MockDevice). Si dice Mock, el lector no está enchufado o el
agente arrancó en Session 0.

## Torniquete (Fase 7, opcional por gym)

Módulo **USB-relé 1 canal** (entrada). Salida = botón mecánico directo al torniquete.
`Agent:TurnstileEnabled = true` + `Agent:RelayPort = COMx`. Verificar bytes ON/OFF del
módulo en `Relays/UsbRelay.cs`.

## Layout

```
src/HuellaAgent/
  Program.cs                 host Kestrel :8000 (solo localhost), DI, CORS
  Config/AgentConfig.cs      puerto, api-key, timeouts, supabase, torniquete
  Api/FingerprintEndpoints.cs los endpoints del contrato + turnstile
  Devices/IFingerprintDevice MockDevice (Linux) · ZkfpDevice (Windows, wrapper zkfp2)
  sdk/win-x64/libzkfpcsharp.dll   wrapper oficial ZKFinger 5.3.0.33 (ref solo en win-x64)
  Storage/LocalFileStore.cs  templates en JSON local (offline-resiliente)
  Relays/IRelay.cs           MockRelay (dev) · UsbRelay (Windows, serial)
  Supabase/HuellaRpc.cs      persistencia durable opcional (anon key + kiosk_token)
tests/HuellaAgent.Tests/     ContractTests + FakeDevice (fija la semántica HTTP)
```

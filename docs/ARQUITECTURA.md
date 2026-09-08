# Arquitectura del HuellaAgent

Cómo encaja cada pieza del agente local del lector **ZKTeco SLK20R**, y qué queda abierto.
El README cubre el setup y el proceso de release; esto cubre el *por qué* de cada decisión
y los hallazgos verificados contra el agente corriendo.

> Verificado en una PC de recepción el **2026-09-06** con agente `v1.0.2` y lector físico.

---

## Por qué corre en la PC y no en el servidor

El SLK20R es un lector **USB**: solo puede leerlo un proceso en la misma máquina donde está
enchufado. Por eso el reconocimiento facial vive en un server remoto y la huella no — son dos
caminos separados a propósito, con variables de entorno distintas
(`VITE_FINGERPRINT_API_URL` vs `VITE_FACE_API_URL`).

La app corre en **https** y le pega a **http**. Eso normalmente lo bloquea el navegador por
contenido mixto, pero `localhost` es un *potentially trustworthy origin* según la spec de
[Secure Contexts](https://www.w3.org/TR/secure-contexts/) del W3C, así que pasa. Es la pieza
que hace viable todo el diseño.

---

## Las cuatro capas

Un dedo apoyado recorre este camino de abajo hacia arriba. Cada frontera falla distinto.

```
┌─ 01 ─ Navegador ──────────────────── src/lib/huellaApi.ts ────────────────┐
│  Chip de estado (polling a /health) · modal de enrolamiento · loop del    │
│  kiosko. El contrato HTTP está LOCKEADO por este archivo: el agente se    │
│  escribió para calzar con él, no al revés.                                │
└───────────────────────────────────────────────────────────────────────────┘
        ↑  HTTP · CORS abierto · x-api-key opcional
┌─ 02 ─ HuellaAgent.exe ────────────── Program.cs (Kestrel) ────────────────┐
│  Ejecutable único autocontenido (~96 MB: trae el runtime .NET adentro).   │
│  ListenLocalhost — nunca se expone a la red. JSON snake_case para calzar  │
│  con el TypeScript. Serilog a archivo porque en prod corre oculto.        │
└───────────────────────────────────────────────────────────────────────────┘
        ↑  P/Invoke · libzkfpcsharp → libzkfp.dll nativa
┌─ 03 ─ ZKFinger SDK 5.3.0.33 ──────── Devices/ZkfpDevice.cs ───────────────┐
│  El wrapper administrado está vendoreado en el repo y solo se referencia  │
│  al compilar para win-x64 (define ZKFP) → el proyecto compila y testea    │
│  igual en Linux y en CI. La .dll nativa la instala setup-driver.exe.      │
└───────────────────────────────────────────────────────────────────────────┘
        ↑  USB · sesión interactiva del usuario
┌─ 04 ─ Lector SLK20R ──────────────── hardware ────────────────────────────┐
│  Sensor óptico. Al abrirlo el SDK negocia exposición del CMOS y ganancia  │
│  de los LEDs (las líneas que escupe la consola al arrancar). Devuelve     │
│  templates en formato nativo ZKFinger — cabecera `MOSS21`.                │
└───────────────────────────────────────────────────────────────────────────┘
```

---

## El truco que hace testeable todo esto

La capa de hardware está detrás de `IFingerprintDevice`, con dos implementaciones:

| | `ZkfpDevice` | `MockDevice` |
|---|---|---|
| Dónde | Windows + `UseRealDevice=true` | Linux, CI, dev |
| Qué hace | SDK real sobre el lector USB | simula capturas y "reconoce" al último enrolado |
| Compila en | solo win-x64 (`#if ZKFP`) | siempre |

Con el mock se prueba el flujo entero —enrolar, identificar, marcar asistencia— sin lector y
sin SDK. `tests/HuellaAgent.Tests/ContractTests.cs` fija la semántica HTTP contra un
`FakeDevice`.

### ⚠️ El fallback silencioso

`DeviceFactory` está escrito así: si el SDK real falla al abrir, **cae al mock en silencio**
en vez de tumbar el proceso.

```csharp
try { return new ZkfpDevice(cfg); }
catch (Exception ex) { log?.LogWarning(ex, "SDK real no disponible; usando MockDevice"); }
return new MockDevice();
```

Eso mantiene el agente vivo para que `/health` pueda contar qué pasó, pero significa que
**`reader: connected` NO prueba que haya un lector**. El campo a mirar siempre es `device`:

- `"ZKTeco SLK20R"` → hardware real
- `"MockDevice (SLK20R simulado)"` → no está leyendo nada

---

## Contrato HTTP

| Endpoint | Auth | Body | Respuesta |
|---|---|---|---|
| `GET /health` | abierto | — | `{ok, reader, device, templates_loaded, durable, version}` |
| `GET /logs?lines=N` | abierto | — | texto · cola del log de Serilog |
| `POST /api/fingerprint/capture` | filtro | `{timeout}` **en segundos** | `{template, quality}` · `408` sin dedo · `503` sin lector |
| `POST /api/fingerprint/enroll` | filtro | `{cliente_id, tenant_id, template1..3}` | `{ok, uid, durable}` · `409` otro gym · `422` merge · `400` |
| `POST /api/fingerprint/identify` | filtro | `{tenant_id}` | `{ok, cliente_id, score}` · `404` sin match · `408` sin dedo |
| `POST /api/pair` | filtro | `{token, supabase_url, anon_key}` | `{ok, tenant_id, gym, templates}` · `401` token inválido |
| `POST /api/turnstile/open` | filtro | `{tenant_id}` | `{ok}` · `409` deshabilitado |

**«filtro» no significa protegido.** `ApiKeyFilter` solo exige el header si `Agent:ApiKey`
está seteada, y el `appsettings.json` del instalador no la trae. Ver
[Riesgos abiertos](#riesgos-abiertos).

---

## Los dos flujos

### Enrolar — escribir

1. El navegador pide **tres capturas** del mismo dedo, una por una.
2. El agente las fusiona con `DBMerge` del SDK en un único template enrolado.
3. Lo guarda en el archivo local. Si el cliente ya tenía huella, **reusa el uid** para que la
   base del SDK quede estable.
4. Si está vinculado, lo sube a Supabase con `huella_enroll`.

Sin vincular responde `durable: false` y el frontend avisa. Antes esto devolvía `ok: true`
igual: la app decía «éxito» y la tabla en la nube quedaba vacía (arreglado en `0c21fc4`).

### Identificar — buscar 1:N

1. Carga en memoria los templates del tenant con `DBInit` + `DBAdd`.
2. Captura un dedo con ventana corta — **4 segundos** por defecto (`IdentifyTimeoutSeconds`).
3. Corre `DBIdentify`, que devuelve el uid ganador y un *score*.
4. Compara el score contra `IdentifyThreshold` y resuelve el `cliente_id`.

El kiosko lo llama en loop. Con el match en mano, la asistencia la marca el propio kiosko
contra `kiosk_marcar_asistencia` — el agente nunca toca eso.

---

## Quién le habla al agente (y por qué compiten)

El agente tiene **un solo sensor** y **tres** consumidores en el navegador. Esto no se ve
desde este repo, pero explica la mayoría de los `identify` que aparecen en `/logs`:

| Consumidor | Dónde vive | Qué pide | Cadencia |
|---|---|---|---|
| **Listener global** | `GymLayout` → **toda** página admin autenticada | `identify` | cada 250 ms |
| **Modal de enrolamiento** | ficha del cliente | `capture` ×3 | a demanda |
| **Kiosko de puerta** | `/kiosko` (excluido del listener global) | `identify` | su propio loop |

El listener global corre en toda página admin a propósito: la idea es que un socio apoye el
dedo y se registre su entrada esté donde esté la recepcionista, aunque esté cobrando en el
POS. La consecuencia es que **enrolar y identificar compiten por el mismo dedo**: el loop de
250 ms gana por frecuencia, se lleva la captura del enrolado y responde 404.

El frontend ya lo coordina con un semáforo (`src/lib/sensorHuella.ts`): el modal reserva el
sensor mientras captura y el listener se apaga. Queda contención residual de hasta
`IdentifyTimeoutSeconds` (~4 s) porque el `identify` **ya en vuelo** no se puede cancelar
desde el cliente — eso lo cierra el scanner continuo (v1.0.3).

### Lo que el agente podría aportar: un `boot_id` en `/health`

El listener descarta el primer `match` tras cada (re)inicio, porque el agente devuelve en su
primer `identify` la huella que tenía bufferada y eso generaba asistencias fantasma. Hoy el
navegador aproxima "el agente se reinició" mirando si `/health` dejó de responder, lo cual
falla si el reinicio dura menos que su ventana de detección.

Con un identificador de arranque en el payload de `/health` —un GUID generado al iniciar el
proceso— el navegador sabría con certeza cuándo re-armar esa guarda. Es un campo y una línea
en `Program.cs`.

> Diagnóstico completo del lado del frontend:
> `administracion_gimnasio-obs/docs/FIX_huella_lector_intermitente_2026-09-07.md`

---

## Dónde viven las huellas

### Local · siempre

`%ProgramData%\HuellaAgent\templates.json`, con forma
`{ tenantId: [ {clienteId, uid, template} ] }`.

- Escritura **atómica**: archivo `.tmp` + `File.Move(overwrite: true)`.
- Copia en memoria, porque el identify pollea cada pocos segundos y releer + deserializar el
  archivo entero en cada poll escalaba linealmente con la cantidad de socios (`6f6cf3c`).
- Sin base de datos, sin claves privilegiadas, y el 1:N funciona **sin internet**.

### Durable · al vincular

RPCs `SECURITY DEFINER` en Supabase (`huella_enroll`, `huella_templates`, `kiosk_init`),
llamadas con la **anon key pública + el kiosk_token** del gym. El agente **nunca** usa la
service-role. Al arrancar vinculado baja los templates del tenant y llena el cache local.

El pairing entra por `POST /api/pair` desde la propia app, se valida contra `kiosk_init` y se
guarda **cifrado con DPAPI (LocalMachine)** en `pairing.dat`. Tiene prioridad sobre
`appsettings` y surte efecto **sin reiniciar** el agente.

---

## Autostart: la lección de campo

Confirmado en campo el 2026-06-15.

### ✅ Tarea programada al iniciar sesión — `instalar-tarea.bat`

Trigger `onlogon`, corre en la **sesión interactiva del usuario** y lanza el exe sin ventana
vía `run-hidden.vbs`. Ahí el USB se ve.

### ❌ Servicio de Windows — `instalar-servicio.bat`

Como `LocalSystem` queda en **Session 0**, donde el SDK de ZKFinger no accede al USB. El
agente **no crashea**: cae al `MockDevice` y sigue reportando `reader: connected`. Todo parece
andar hasta que ninguna huella coincide con nadie.

> Por eso la verificación obligatoria post-instalación no es «¿responde el agente?» sino
> **«¿qué dice `device`?»**.

---

## Cómo llega una versión nueva a cada gym

```
// al iniciar sesión, ANTES de que el agente arranque
run-hidden.vbs  →  launch.ps1
                     ├─ consulta el último release de fitgym-huella-agent-dist
                     ├─ si el tag > version.txt local:
                     │    baja el ZIP · guarda HuellaAgent.exe.bak · reemplaza el exe
                     └─ lanza el agente oculto

// sin internet o si GitHub falla, sigue con la versión que tiene
```

El swap del exe es seguro justamente porque corre *antes* de que el agente levante.

El botón «Descargar agente» de la app apunta a `/releases/latest/download/HuellaAgent-Setup.zip`,
un **puntero flotante**: publicar un release nuevo cambia lo que se baja sin tocar la app ni
redeployar. La contracara es que un build roto se empuja a todos los gimnasios a la vez, y el
único rollback es el `.bak` a mano.

---

## Riesgos abiertos

### 🔴 Crítico — el umbral de identificación está en cero

`IdentifyThreshold` vale `0` por defecto (`Config/AgentConfig.cs`) y el `appsettings.json` del
instalador no lo pisa. Como el chequeo es:

```csharp
if (score < _threshold) return null;   // bajo el umbral calibrado (R1)
```

…**cualquier match que devuelva el SDK pasa**, por bajo que sea su score. Es la R1 que el
README marca como pendiente de calibrar con dedos reales antes de un piloto: admitir al
cliente equivocado es el peor fallo posible del producto.

### 🟠 Alto — la api-key no filtra nada

Verificado contra el agente corriendo: `capture` devuelve `200` sin mandar ninguna clave, y
también con una inventada. Sumado a `Access-Control-Allow-Origin: *`, cualquier página abierta
en el navegador de esa PC puede pedirle capturas al lector.

Se cierra seteando `Agent:ApiKey` en el `appsettings.json` del instalador y
`VITE_FINGERPRINT_API_KEY` en el frontend con el mismo valor.

### 🟠 Medio — dos capturas concurrentes se pelean por el mismo dedo

Reproducido: con dos requests de captura abiertas, una se come el dedo y la otra agota su
ventana. Es exactamente lo que arregla el parche de **escáner continuo (v1.0.3)**, todavía sin
aplicar.

### 🔵 A saber — el `timeout` de capture está en segundos

No en milisegundos.

```csharp
var r = await device.CaptureAsync(body?.Timeout ?? 15, ct);
```

Mandarle `60000` pone al agente a esperar casi **diecisiete horas** sin devolver nunca el
`408` y sin dejar rastro en el log. Peor: la request queda viva del lado del servidor aunque
el cliente corte, con el loop de adquisición agarrado — y se roba todos los dedos siguientes.
Si el lector "dejó de leer", revisar conexiones abiertas al `:8000` antes de culpar al
hardware.

---

## Verificar una instalación

```bash
# 1. ¿Está el agente y con qué dispositivo?
curl.exe -s http://localhost:8000/health
# → device debe decir "ZKTeco SLK20R", NO MockDevice

# 2. ¿Windows reconoció el lector?  (Status debe ser OK, no Error)
powershell -Command "Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match 'SLK' }"

# 3. ¿Lee un dedo?  (apoyar el dedo ANTES de correrlo)
curl.exe -s -m 20 -X POST http://localhost:8000/api/fingerprint/capture
# → {"template":"TU9TUzIx...","quality":NN}   ← MOSS21 en base64 = template real
# → {"detail":"timeout sin dedo"} + 408       ← no leyó

# 4. ¿Arranca solo?
schtasks /query /tn "FitGymHuellaAgent"

# 5. Logs sin buscar el archivo (el agente corre oculto)
curl.exe -s "http://localhost:8000/logs?lines=50"
```

---

## Layout del repo

```
src/HuellaAgent/
  Program.cs                    host Kestrel :8000, DI, CORS, Serilog
  Config/AgentConfig.cs         puerto, api-key, umbrales, supabase, torniquete
  Config/PairingStore.cs        pairing cifrado con DPAPI
  Api/FingerprintEndpoints.cs   los endpoints del contrato + filtro de api-key
  Devices/IFingerprintDevice    la abstracción del lector
  Devices/ZkfpDevice.cs         SDK real · solo se compila en win-x64
  Devices/MockDevice.cs         simulador · Linux, CI, dev
  Storage/LocalFileStore.cs     templates.json con cache en memoria
  Supabase/HuellaRpc.cs         persistencia durable · anon key, nunca service-role
  Relays/UsbRelay.cs            torniquete por puerto serie (fase 7)
  sdk/win-x64/libzkfpcsharp.dll wrapper oficial ZKFinger 5.3.0.33

scripts/windows/
  instalar.bat                  todo en uno: driver + agente + autostart
  instalar-tarea.bat            solo la tarea al logon  ← la que sirve
  instalar-servicio.bat         servicio  ← no funciona con el SLK20R
  launch.ps1                    auto-update desde GitHub + lanzar
  run-hidden.vbs                corre el exe sin ventana

tests/HuellaAgent.Tests/        ContractTests + FakeDevice: fijan la semántica HTTP
hardware/arduino/               sketches del relé del torniquete
```

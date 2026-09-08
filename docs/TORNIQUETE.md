# Torniquete — guía de instalación

Cómo conectar un torniquete al agente para que se abra solo cuando el gate de membresía
concede el paso. Verificado end-to-end el **2026-09-08** con hardware real.

Hardware de referencia de esta instalación:

| Pieza | Modelo |
|---|---|
| Torniquete | **ZKTeco TS1000** (trípode), placa `SATT-V1.4` |
| Relé | **LCUS-1** — Songle `SRD-05VDC-SL-C` sobre placa con CH340 y USB-A integrado |
| Lector | ZKTeco SLK20R (USB) |

---

## No hace falta panel de control de acceso

El manual del TS1000 asume la arquitectura clásica de ZKTeco:

```
Lectores RS485/Wiegand  →  Panel de Control de Acceso  →  K1/GND del torniquete
```

Ese panel guarda una lista de tarjetas, decide si la persona está autorizada y cierra su
relé. **Nosotros no lo usamos: la PC con la app ocupa su lugar.**

```
SLK20R (USB)  →  PC  →  HuellaAgent  →  relé USB  →  K1/GND del torniquete
```

`useRegistrarAsistencia.registrarCliente` hace lo que haría el panel, y el relé USB es su
salida. Para un gimnasio sale ganando: el panel decidiría contra una lista cargada a mano,
y la app decide contra la membresía real —vencimiento, cupo de clases, la sede correcta— y
deja todo auditado en Supabase en vez de en la memoria de una cajita.

Comprobado en campo: un socio con membresía vencida quedó identificado por huella, se
registró en `accesos_denegados` con motivo `expired`, y el relé no se movió.

---

## Cableado

Las entradas de apertura del TS1000 son **`K1` + `GND`** y **`K2` + `GND`**, una por
dirección. Se abren al **cerrarse** el circuito, y exigen **contacto seco** — sin tensión.

> El manual es explícito: *«Do not use electrically charged objects to connect to the port
> of Opening signal input, otherwise it will damage the control board.»*

Un relé mecánico es exactamente eso: sus contactos están aislados del USB y de la PC. No
cruza ni un volt.

### Varios disparadores por entrada, en paralelo

```
                  ┌──── relé USB (NO) ─────────┐
K1  (entrada) ────┤                            ├──── GND
                  └──── botón recepción (NO) ──┘

K2  (salida)  ─────────── botón salida (NO) ────────── GND
```

Cualquier contacto que cierre dispara la apertura. Sin resistencias ni fuentes: los tres
son contactos secos.

### ⚠️ Siempre NO, nunca NC

Tanto el relé como los botones van por su contacto **normalmente abierto**.

Con NC el circuito está cerrado en reposo, así que el torniquete recibiría la señal de
apertura **de forma permanente** —libre para cualquiera— y apretar el botón la cortaría.
Justo al revés.

### Lo que NO hay que tocar

| Borne | Qué es |
|---|---|
| `V1L` `V1+` · `V2R` `V2+` · `UP-` `UP+` | **Solenoides** — salidas de potencia que mueven el mecanismo |
| `L1G` `L1R` `L1COM` · `L2G` `L2R` `L2COM` | Indicadores LED de dirección |
| `G_R` `R_X` `LCOM` | Indicador de paso |
| `COM1` `NO1` · `COM2` `NO2` | Salidas de contador |
| `SEN±` `SEN1/2` · `SENC1/2/3` | Board optoacoplador |
| `GND` `DOW` · `GND` `ALARM` | Switch de emergencia |
| `GND` `+DC24V` | Alimentación (DC24V) |
| `485A` `485B` `485COM` | RS485 (reservado) |

Los solenoides son el error caro: mueven el brazo, no lo mandan.

---

## DIP switches

**Abajo = 1, arriba = 0.**

| Pin | Función | Valor | Por qué |
|---|---|---|---|
| 1-2-3 | Duración de apertura | `000` = 5 s | Default de fábrica, alcanza. Si la gente duda y se traba, `100` = 10 s |
| 4-5 | Dirección | `01` | Único valor que habilita ambos sentidos (`00` y `10` son de una sola dirección). Switch 4 arriba, switch 5 abajo |
| 6 | Función de memoria | **apagado** | Ver abajo. Viene deshabilitada de fábrica: no tocar |

### Por qué la memoria va apagada

La función de memoria acumula hasta **20 permisos de paso** y deja pasar a 20 personas sin
autenticarse. En un gimnasio eso son créditos guardados: alguien se autentica a las 8 de la
mañana y el permiso queda en el banco para que entre otro más tarde.

La contra honesta: en hora pico, dos socios que se autentican con muy poco margen pueden
pisarse y el segundo tiene que volver a apoyar el dedo. Entre eso y pases sueltos en el
banco, gana lo primero.

### ⚠️ Hay DOS "duración de apertura" y es fácil confundirlas

| En el manual | Qué es | Dónde se configura |
|---|---|---|
| **#1 Duración de Apertura** | El pulso del relé del panel | `RelayPulseMs` del agente |
| **#5 Duración de Apertura del Torniquete** | Cuánto queda liberado el brazo | DIP 1-2-3 |

La nota del Anexo 1 que dice *«configúrela a 1 segundo»* se refiere a la **#1**, el pulso
del relé. Si alguien la lee rápido y pone los DIP en 1 segundo, el socio no llega a empujar
el brazo.

---

## Configuración del agente

```json
"Agent": {
  "TurnstileEnabled": true,
  "RelayPort": "COM3",
  "RelayPulseMs": 700
}
```

`RelayPulseMs` **debe ser ≤ 1000**. El manual lo pide dos veces:

> *Precaución: La activación del relevador del panel de acceso debe ser igual o menor de
> 1 segundo.*

Los 700 ms entran con margen. No hace falta subirlos "para dar más tiempo de pasar": el
tiempo de paso lo decide la placa por sus DIP, no el pulso.

`launch.ps1` solo reemplaza el `.exe` y el `version.txt` al auto-actualizar, así que esta
config sobrevive a las actualizaciones.

### El puerto COM es exclusivo

Mientras el agente corre con `TurnstileEnabled`, mantiene el COM abierto y no lo suelta
(`UsbRelay` abre el `SerialPort` en el constructor). El botón «Conectar puerta» del kiosko
—que usa Web Serial contra el mismo tipo de placa— **no puede tomar ese puerto a la vez**.
Son caminos alternativos, no complementarios.

---

## El botón de recepción: úsalo como respaldo, no como herramienta diaria

Ese botón abre el torniquete **sin registrar nada**. La persona entra y no queda asistencia,
ni auditoría, ni suma al «Dentro del gym ahora». Usado seguido, los números se despegan de
la realidad y no hay forma de saber quién entró.

Y para el caso normal **no hace falta**: en Asistencia, la pestaña **DNI** busca por nombre,
código o documento, y `registrarPorDNI` pasa por el mismo gate y dispara el relé. Mismo
resultado, con datos.

Su justificación real es otra: **cuando la PC o el agente están caídos**. Ahí es
imprescindible, porque sin él no entra nadie. Conviene montarlo en un lugar deliberadamente
menos cómodo —dentro del mostrador, no encima— para que el camino fácil sea el que deja
registro.

El botón de salida, en cambio, está bien sin registro: nadie marca asistencia para irse. Lo
único a cuidar es que quede fuera del alcance desde afuera.

---

## Verificación

Antes de atornillar el relé, el propio manual documenta la prueba:

> *3. Realice una conexión en puertos «K1, GND» y/o «K2, GND», si el torniquete abre, puede
> ser un problema del panel de acceso.*

1. **Puente manual.** Con el torniquete encendido, tocar un cable entre `K1` y `GND`. El
   brazo debe liberarse. Si libera, ahí va el relé.
2. **Pulso del agente**, sin cablear nada — solo hay que oír el clic:
   ```bash
   curl.exe -s -X POST -H "Content-Type: application/json" \
     -d "{\"tenant_id\":\"test\"}" http://localhost:8000/api/turnstile/open
   ```
   El log del agente debe decir `UsbRelay: pulso de 700 ms enviado a COMx`. Si dice
   `MockRelay`, el COM no abrió — revisar `RelayPort`.
3. **Caso positivo.** Socio con membresía vigente apoya el dedo → asistencia + pulso.
4. **Caso negativo — el que importa.** Socio con membresía **vencida** apoya el dedo → fila
   en `accesos_denegados` con motivo `expired` y **el relé no se mueve**. Sin esta prueba,
   "abre para quien debe" no dice nada sobre lo único que importa antes de colgarlo de una
   puerta.

Resultados de la instalación de referencia: 3 socios vigentes → 3 pulsos, latencia de
1.5-2 s (viene del ciclo de `identify`, no del relé). 1 vencido → relé mudo.

---

## En caso de emergencia

> *Los brazos del torniquete caerán de forma automática en caso de falla eléctrica y
> permitirán el paso libre al público.*

Corte de luz = brazos caen = paso libre. Es **fail-safe** por evacuación, y va en dirección
contraria a nuestro relé, que es fail-secure. Las dos decisiones son correctas y no se
contradicen: el relé no debe abrir solo, y el torniquete debe liberarse si se queda sin
energía.

Al restaurar la alimentación: esperar **al menos 6 segundos** antes de levantar los brazos a
mano.

El grupo `GND`/`DOW` y `GND`/`ALARM` es el switch de emergencia de la placa — mantiene el
torniquete abierto ante una señal de incendio. No lo usamos, pero conviene saber que existe
si el gimnasio tiene que integrarlo con su sistema contra incendios.

---

## Pendiente

- **El pulso a un relé desconectado devuelve 500.** `FingerprintEndpoints` llama a
  `relay.PulseAsync` sin `try/catch`, así que si el relé se desenchufa o el cable se afloja,
  cada entrada tira un error sin manejar y nadie se entera de que la puerta dejó de
  responder. Debería devolver `503` con detalle, como ya hace el lector
  (`DeviceUnavailableException`). Y `abrirTorniquete` del frontend sólo se auto-desactiva
  ante un `409`, no ante un `500`, así que insiste indefinidamente.

# La configuración con la que sale el agente

`appsettings.produccion.json` es **lo que se instala en la PC de un gimnasio**. Lo copia
`scripts/publicar.ps1` dentro del ZIP, como `appsettings.json`.

## Por qué existe este archivo

Hasta el 23-sep no existía. El ZIP se armaba a mano y `src/HuellaAgent/appsettings.json`
—el de desarrollo— dice `"UseRealDevice": false`. O sea que quien empaquetaba tenía que
acordarse de editarlo a `true` antes de comprimir.

Si esa edición sale mal, **el agente arranca con `MockDevice` en todos los gimnasios que
instalen ese ZIP**. Y no es un error que grite: el agente queda vivo, `/health` contesta,
y sólo `device` dice `MockDevice (SLK20R simulado)`. El panel lo detecta —desde el 17-sep
un lector simulado se muestra como desconectado— pero nadie sabría por qué.

Ahora la config de producción es **un archivo versionado, que se revisa en un diff**, y hay
un test (`PaqueteTests`) que falla si alguien lo deja en `false`.

## Por qué tiene tan poco adentro

Todo lo demás vive con su valor por defecto en `AgentConfig.cs`, que es el único lugar
donde está documentado qué significa cada cosa. Repetir un valor acá lo duplica: el día
que cambie el default, esta copia lo pisa en silencio.

Sólo van los que **tienen que ser distintos en producción**:

| Clave | Por qué está acá |
|---|---|
| `UseRealDevice` | el default es `false` para que los tests y el desarrollo corran con `MockDevice` sin lector. En una PC de recepción tiene que ser `true` o no lee nada |
| `Port` | el frontend lo tiene fijo en `huellaApi.ts` (`localhost:8000`). Explícito para que se vea, no para cambiarlo |

## Lo que NO va acá

La configuración **de cada gimnasio** —si tiene torniquete, en qué puerto, si la puerta
abre sin navegador— no puede vivir en un archivo de una PC: el día que el gym cambia de
computadora hay que reproducirla a mano, y eso obliga a una llamada. Va a viajar con el
vínculo (HU-17 del PRD 103), como ya viajan las huellas.

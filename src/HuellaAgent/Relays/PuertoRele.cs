using System.IO.Ports;
using System.Runtime.InteropServices;

namespace HuellaAgent.Relays;

/// <summary>Cómo se llegó a un puerto: importa para saber cuánto confiar en él.</summary>
public enum OrigenDelPuerto
{
    /// <summary>Lo puso una persona en appsettings.json. Manda siempre.</summary>
    Configurado,
    /// <summary>Se encontró un CH340 y un solo puerto serie. Es el caso normal.</summary>
    Detectado,
    /// <summary>Hay un solo puerto pero no se confirmó que sea el relé.</summary>
    Unico,
    /// <summary>No hay ninguno, o hay varios y no se puede elegir sin adivinar.</summary>
    Ninguno,
}

public sealed record PuertoElegido(string? Port, OrigenDelPuerto Origen, string Motivo);

/// <summary>
/// Encuentra solo el puerto COM del módulo USB-relé.
///
/// POR QUÉ EXISTE. Era el último dato que obligaba a que alguien fuera hasta la PC: para
/// configurar el torniquete había que abrir el Administrador de dispositivos, buscar en
/// "Puertos (COM y LPT)" cuál le tocó al adaptador, y escribirlo a mano en un archivo de
/// texto. Un recepcionista no hace eso, así que cada cambio de computadora terminaba en
/// una llamada.
///
/// Y es el ÚNICO de la configuración que no se puede resolver desde el servidor: el número
/// de puerto depende de esa máquina y de en qué USB lo enchufaron. Todo lo demás —si el
/// gimnasio tiene torniquete, cuánto dura el pulso, si la puerta abre sin navegador— es
/// del gimnasio y viaja con el vínculo (HU-17 del PRD 103).
///
/// CÓMO. El módulo que usamos es un LCUS con chip CH340 (QinHeng), que tiene VID conocido.
/// Se pregunta por un lado qué puertos serie hay (`SerialPort.GetPortNames`) y por otro si
/// Windows ve un CH340 enchufado, y se cruzan las dos respuestas.
///
/// LO QUE ESTO NO HACE. No mapea el CH340 a SU puerto: eso necesitaría leer el registro
/// (`Device Parameters\PortName`) y una dependencia más. Con un solo puerto serie —que es
/// el caso de cualquier PC de recepción— no hace falta. Con varios NO adivina: devuelve
/// `Ninguno` con el motivo, y ahí sí hace falta escribirlo a mano. Es mejor pedir ayuda en
/// el 1% que pulsar el relé equivocado en el 100%.
/// </summary>
public static class PuertoRele
{
    /// <summary>VID de QinHeng (CH340/CH341), el chip de los módulos USB-relé tipo LCUS.</summary>
    public const string VidCh340 = "VID_1A86";

    /// <summary>
    /// Elige el puerto. Lo que alguien escribió a mano manda, pero SÓLO si ese puerto todavía
    /// existe: obedecer un dato que ya no apunta a nada no es respetar una decisión, es
    /// repetir un error. Ver el comentario de adentro.
    /// </summary>
    public static PuertoElegido Elegir(string? configurado, Func<string[]>? puertos = null, Func<bool>? hayCh340 = null)
    {
        var lista = (puertos ?? SerialPort.GetPortNames)().Distinct().OrderBy(p => p).ToArray();
        var ch340 = (hayCh340 ?? HayCh340)();

        var aMano = configurado?.Trim();
        if (!string.IsNullOrWhiteSpace(aMano))
        {
            // Lo escrito a mano gana, PERO solo si ese puerto existe.
            //
            // Visto en campo el 23-sep: Windows le cambia el numero al adaptador cada vez
            // que se lo reenchufa. Una PC configurada con COM3 volvio como COM4 y el
            // torniquete dejo de abrir, mientras la deteccion —que lo habria encontrado al
            // instante— quedaba bloqueada justamente por ese valor viejo.
            //
            // "Respetar lo que alguien configuro" no puede significar obedecer un dato que
            // ya no apunta a nada. Si el puerto esta, manda; si no esta, se detecta y queda
            // dicho en el motivo para que nadie lo persiga.
            if (lista.Contains(aMano, StringComparer.OrdinalIgnoreCase))
                return new PuertoElegido(aMano, OrigenDelPuerto.Configurado, "puesto a mano en la configuración");

            if (lista.Length == 1)
                return new PuertoElegido(lista[0], ch340 ? OrigenDelPuerto.Detectado : OrigenDelPuerto.Unico,
                    $"{aMano} ya no existe (Windows le cambia el numero al reenchufarlo); se detecto {lista[0]}");
        }

        if (lista.Length == 0)
            return new PuertoElegido(null, OrigenDelPuerto.Ninguno,
                ch340 ? "Windows ve un CH340 pero no aparece ningún puerto serie: falta el driver"
                      : "no hay ningún puerto serie: el módulo del relé no está enchufado");

        if (lista.Length == 1)
            return ch340
                ? new PuertoElegido(lista[0], OrigenDelPuerto.Detectado, $"único puerto serie y hay un CH340 enchufado ({lista[0]})")
                : new PuertoElegido(lista[0], OrigenDelPuerto.Unico, $"único puerto serie, pero no se confirmó que sea el relé ({lista[0]})");

        // Varios puertos: elegir uno seria adivinar, y adivinar mal significa escribirle
        // bytes a otro aparato. Que lo diga una persona.
        return new PuertoElegido(null, OrigenDelPuerto.Ninguno,
            $"hay {lista.Length} puertos serie ({string.Join(", ", lista)}): escribí cuál es el del relé en RelayPort");
    }

    // ── ¿Windows ve un CH340? ────────────────────────────────────────────────────
    // Mismo camino que UsbPresencia usa para el lector: cfgmgr32 cuesta menos de un
    // milisegundo y no necesita WMI ni System.Management.

    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const int CR_SUCCESS = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int pulLen, string pszFilter, int ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] buffer, int bufferLen, int ulFlags);

    /// <summary>
    /// true = hay un CH340 presente. En Linux/CI devuelve false sin intentar nada: el
    /// torniquete es Windows-only y ahí siempre corre el MockRelay.
    /// </summary>
    public static bool HayCh340()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            const int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out int len, "USB", flags) != CR_SUCCESS) return false;
            if (len <= 1) return false;

            var buf = new char[len];
            if (CM_Get_Device_ID_ListW("USB", buf, len, flags) != CR_SUCCESS) return false;
            return new string(buf).Contains(VidCh340, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A diferencia de UsbPresencia —que ante la duda dice que SÍ hay lector, para no
            // acusarlo de desenchufado— acá la duda se resuelve al revés: sin confirmación,
            // el puerto queda como `Unico` y no como `Detectado`. Peor es afirmar de más.
            return false;
        }
    }
}

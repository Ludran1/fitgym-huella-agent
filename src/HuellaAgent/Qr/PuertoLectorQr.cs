using HuellaAgent.Relays;

namespace HuellaAgent.Qr;

/// <summary>Qué puerto se eligió para el lector QR y por qué (va al log y a /health).</summary>
public sealed record PuertoQrElegido(string? Puerto, string Motivo);

/// <summary>
/// Qué puerto COM es el del lector QR de mostrador.
///
/// POR QUÉ IMPORTA. Hasta la v1.7 el único puerto serie que le interesaba al agente era el
/// del relé, y la detección de PuertoRele se apoya en eso: con UN solo puerto serie en la PC,
/// ése es el relé. Un lector QR en modo COM es un segundo puerto serie, y sin separarlos el
/// agente podía tomar el lector por relé —ni abre la puerta ni se lee el QR— o escuchar al
/// relé como si fuera el lector. Cada uno se reconoce por el aparato que Windows dice que hay
/// del otro lado del puerto, y ninguno toma el del otro.
///
/// LO QUE NO HACE. No adivina. Un puerto USB de un aparato que no es un lector conocido no se
/// abre aunque sea el único: un puerto serie lo usa un programa por vez, así que el agente lo
/// tendría tomado mientras corre y ese aparato —una balanza, otro lector— dejaría de andar
/// sin que nadie sepa por qué. Para un lector de otra marca está `QrPort`; y el motivo lista
/// los puertos USB que no reconoció, que es lo que hace falta para sumar su VID acá.
/// </summary>
public static class PuertoLectorQr
{
    /// <summary>
    /// Lectores reconocidos por el VID del fabricante. Cada uno es un modelo que se vio
    /// funcionar en modo COM: VID_9901 es el NETUM de mostrador (omnidireccional, cabeza
    /// redonda) que se conectó el 10-oct en la PC de Adriano.
    /// </summary>
    public static readonly string[] VidsConocidos = { "VID_9901" };

    /// <param name="configurado">`QrPort` del appsettings: si existe, manda.</param>
    /// <param name="usb">Los puertos serie de aparatos USB enchufados (PuertosSerieUsb).</param>
    /// <param name="existentes">Los puertos serie que ve Windows ahora (SerialPort.GetPortNames).</param>
    public static PuertoQrElegido Elegir(string? configurado, IReadOnlyList<PuertoUsb> usb, IReadOnlyCollection<string> existentes)
    {
        var aMano = configurado?.Trim();
        if (!string.IsNullOrWhiteSpace(aMano) && existentes.Contains(aMano, StringComparer.OrdinalIgnoreCase))
            return new PuertoQrElegido(aMano.ToUpperInvariant(), "puesto a mano en la configuración (QrPort)");

        // Lo escrito a mano que ya no existe no frena la detección: Windows le cambia el
        // número a un aparato cuando lo enchufan en otro USB (pasó con el relé el 23-sep).
        var antes = string.IsNullOrWhiteSpace(aMano) ? "" : $"{aMano} (QrPort) ya no existe; ";

        var lectores = DeLectores(usb)
            .Where(p => existentes.Contains(p.Puerto, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p.Puerto, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (lectores.Length > 0)
            return new PuertoQrElegido(lectores[0].Puerto, antes + (lectores.Length == 1
                ? $"lector QR en {lectores[0]}"
                : $"hay {lectores.Length} lectores QR; se usa {lectores[0]}"));

        var sinReconocer = usb.Where(p => !EsDelRele(p)).ToArray();
        return new PuertoQrElegido(null, antes + (sinReconocer.Length == 0
            ? "no hay ningún lector QR en modo COM"
            : $"no hay un lector QR conocido en modo COM (puertos USB sin reconocer: {string.Join(", ", sinReconocer.Select(p => p.ToString()))})"));
    }

    /// <summary>Los puertos que son de un lector QR conocido. Nunca el del relé.</summary>
    public static IEnumerable<PuertoUsb> DeLectores(IReadOnlyList<PuertoUsb> usb) =>
        usb.Where(p => !EsDelRele(p) && VidsConocidos.Contains(p.Vid, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Los puertos serie SIN los del lector QR: es lo que puede mirar la detección del relé.
    /// Con el lector enchufado, "el único puerto serie" vuelve a ser el del relé.
    /// </summary>
    public static string[] SinLosDelLector(IEnumerable<string> todos, IReadOnlyList<PuertoUsb> usb, string? qrPort)
    {
        var delLector = DeLectores(usb).Select(p => p.Puerto).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(qrPort)) delLector.Add(qrPort.Trim());
        return todos.Where(p => !delLector.Contains(p)).ToArray();
    }

    private static bool EsDelRele(PuertoUsb p) => p.Vid.Equals(PuertoRele.VidCh340, StringComparison.OrdinalIgnoreCase);
}

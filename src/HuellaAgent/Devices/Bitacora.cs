namespace HuellaAgent.Devices;

/// <summary>
/// Las ultimas decisiones del portero, para poder verlas DESDE AFUERA del gimnasio.
///
/// POR QUE EXISTE. El 26-sep un rele colgado mato al portero en recepcion. El log del
/// agente decia exactamente que habia pasado —"pasa Adriano score=669" y despues ni una
/// linea mas— pero ese log vive en `C:\ProgramData` de esa PC. Diagnosticarlo costo ir
/// preguntando de a una las cosas que se podian mirar desde la base, y adivinar mal dos
/// veces por el camino. Con estos eventos viajando en el latido se habria visto en cinco
/// segundos desde super-admin.
///
/// QUE NO ES. No es "mandar el log". El log del agente tiene megas por dia y nombres de
/// socios en cada linea. Esto son las ultimas <see cref="Capacidad"/> DECISIONES, que es
/// lo unico que se necesito: que paso, cuando, con cuanto puntaje y por que no abrio.
/// Ocupa un par de kilobytes y se reemplaza entero en cada latido, asi que no crece.
/// </summary>
public sealed class Bitacora
{
    /// <summary>Cuantos eventos se guardan. ~25 cubre un rato largo de puerta.</summary>
    public const int Capacidad = 25;

    private readonly Queue<Evento> _cola = new();
    private readonly object _gate = new();

    /// <param name="que">
    /// Que paso, en una palabra: <c>paso</c>, <c>sin_coincidencia</c>, <c>rechazado</c>,
    /// <c>sin_servidor</c>, <c>rele_no_abrio</c>, <c>rele_colgado</c>.
    /// </param>
    /// <param name="metodo">
    /// Por dónde llegó: <c>huella</c> o <c>qr</c> (desde la v1.8 la puerta la usan los dos).
    /// </param>
    public void Anotar(string que, string? quien = null, int? score = null, string? motivo = null, string? metodo = null)
    {
        var e = new Evento(DateTime.UtcNow, que, quien, score, motivo, metodo);
        lock (_gate)
        {
            _cola.Enqueue(e);
            while (_cola.Count > Capacidad) _cola.Dequeue();
        }
    }

    /// <summary>Del mas viejo al mas nuevo. Copia: el latido lo serializa sin el lock.</summary>
    public IReadOnlyList<Evento> Ultimos()
    {
        lock (_gate) return _cola.ToArray();
    }

    /// <param name="Quien">
    /// El nombre que devolvio el servidor. Va porque quien mira esto ya ve a ese socio en el
    /// panel, y sin nombre la pregunta util ("¿por que a Juan no le abre?") no se contesta.
    /// Nunca viaja el template ni el id de la persona.
    /// </param>
    public sealed record Evento(DateTime EnUtc, string Que, string? Quien, int? Score, string? Motivo, string? Metodo = null);
}

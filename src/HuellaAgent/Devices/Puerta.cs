using HuellaAgent.Config;
using HuellaAgent.Relays;
using HuellaAgent.Supabase;

namespace HuellaAgent.Devices;

/// <summary>
/// Lo que pasa en la puerta una vez que el servidor decidió: el aviso sonoro, el torniquete
/// y la bitácora.
///
/// Hasta la v1.7 vivía adentro del portero de la huella. Desde la v1.8 también la usa el
/// lector QR, y por eso es una pieza aparte que atiende DE A UNO: si un dedo y un QR llegan
/// juntos, cada uno tiene su pulso entero del relé y su aviso, en vez de pisarse (el
/// "cerrar" del primero cortándole el giro al segundo).
/// </summary>
public sealed class Puerta
{
    private readonly IRelay _relay;
    private readonly Bocina _bocina;
    private readonly ConfigDelGimnasio _gym;
    private readonly Bitacora _bitacora;
    private readonly ILogger<Puerta> _log;
    private readonly SemaphoreSlim _deAUno = new(1, 1);

    public Puerta(IRelay relay, Bocina bocina, ConfigDelGimnasio gym, Bitacora bitacora, ILogger<Puerta> log)
    {
        _relay = relay;
        _bocina = bocina;
        _gym = gym;
        _bitacora = bitacora;
        _log = log;
    }

    /// <param name="veredicto">Lo que contestó el servidor. null = no contestó.</param>
    /// <param name="metodo">"huella" o "qr": va al log y a la bitácora.</param>
    /// <param name="score">Puntaje de la huella; null para el QR.</param>
    public async Task AtenderAsync(HuellaRpc.Veredicto? veredicto, string metodo, int? score, CancellationToken ct)
    {
        await _deAUno.WaitAsync(ct);
        try
        {
            if (veredicto is null)
            {
                // Sin veredicto no se abre. Es la decisión correcta mientras no exista la
                // copia local de vigencias: preferimos que el socio pase por recepción a
                // abrirle la puerta a cualquiera porque se cayó internet.
                _log.LogWarning("Portero ({Metodo}): sin respuesta del servidor; NO se abre (score {Score})", metodo, score);
                _bitacora.Anotar("sin_servidor", score: score, metodo: metodo);
                return;
            }

            if (!veredicto.AbrirPuerta)
            {
                _log.LogInformation("Portero ({Metodo}): {Nombre} NO pasa ({Motivo}) score={Score}",
                    metodo, veredicto.Nombre ?? "(desconocido)", veredicto.Motivo, score);
                _bitacora.Anotar("rechazado", veredicto.Nombre, score, veredicto.Motivo, metodo);
                _bocina.Rechazo();
                return;
            }

            _log.LogInformation("Portero ({Metodo}): pasa {Nombre} ({Tipo}{YaHoy}) score={Score}",
                metodo, veredicto.Nombre, veredicto.Tipo, veredicto.YaHoy ? ", ya habia entrado hoy" : "", score);
            _bitacora.Anotar("paso", veredicto.Nombre, score, veredicto.Tipo, metodo);
            // El equipo suena distinto y más bajo: recepción sabe sin mirar que no fue un socio.
            if (veredicto.Tipo == "staff") _bocina.Equipo(); else _bocina.Ok();

            if (!_gym.Actual.TieneTorniquete) return;   // gym sin torniquete: solo se registra
            await AbrirSinJugarseElPortero(metodo, ct);
        }
        finally
        {
            _deAUno.Release();
        }
    }

    /// <summary>
    /// Abre el torniquete sin arriesgar el portero.
    ///
    /// EL 26-SEP UN RELE COLGADO MATO AL PORTERO. `SerialPort.Write` es bloqueante y sin
    /// timeout no vuelve nunca; como corría en el hilo del portero, se quedó trabado ahí
    /// para siempre: dejó de mirar dedos, sin un error en el log, mientras /health seguía
    /// diciendo `turnstile: ready`. El síntoma que llegó fue "cambié el lector y no abre".
    ///
    /// La causa ya está arreglada donde corresponde (UsbRelay pone WriteTimeout). Esto es la
    /// otra mitad: que NINGUNA falla de un periférico pueda volver a matar al portero, la haya
    /// previsto yo o no. Un hilo abandonado es infinitamente mejor que una puerta muerta, y
    /// con el timeout puesto no debería pasar nunca.
    ///
    /// Va por Task.Run a propósito: PulseAsync escribe al puerto ANTES de su primer await,
    /// así que llamarlo directo ya bloquea a quien lo llama y el techo no llegaría a correr.
    /// </summary>
    private async Task AbrirSinJugarseElPortero(string metodo, CancellationToken ct)
    {
        var pulso = Task.Run(() => _relay.PulseAsync(_gym.Actual.PulsoMs, ct), ct);
        var techo = Task.Delay(TechoPulso, ct);

        if (await Task.WhenAny(pulso, techo) == techo)
        {
            _log.LogError("Portero: el rele no contesto en {Seg}s; sigo atendiendo sin el", TechoPulso.TotalSeconds);
            _bitacora.Anotar("rele_colgado", motivo: $"no contesto en {TechoPulso.TotalSeconds:F0}s", metodo: metodo);
            return;   // el pulso queda abandonado; el portero sigue vivo, que es lo que importa
        }

        try { await pulso; }
        catch (Exception ex)
        {
            // La asistencia YA quedó registrada: que la puerta falle no debe borrarla.
            _log.LogError(ex, "Portero: el acceso se concedio pero el rele no abrio");
            _bitacora.Anotar("rele_no_abrio", motivo: ex.Message, metodo: metodo);
        }
    }

    /// <summary>Techo del pulso: de sobra para 4 bytes a 9600 baudios (~4 ms).</summary>
    private static readonly TimeSpan TechoPulso = TimeSpan.FromSeconds(5);
}

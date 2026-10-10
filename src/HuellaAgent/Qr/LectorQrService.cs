using System.IO.Ports;
using System.Threading.Channels;
using HuellaAgent.Config;
using HuellaAgent.Devices;
using HuellaAgent.Supabase;

namespace HuellaAgent.Qr;

/// <summary>
/// El lector QR de mostrador, leído por el agente (v1.8, 10-oct).
///
/// Hasta acá el lector se usaba como TECLADO: escribía el código y un Enter donde estuviera
/// el cursor. Sólo marcaba si alguien dejaba el Kiosko abierto y adelante en la pantalla, y si
/// recepción estaba en WhatsApp el Enter mandaba el código como mensaje (el mismo 10-oct los
/// códigos escaneados terminaron en un chat). En modo COM el lector no escribe en ninguna
/// ventana: lo lee el agente y hace lo mismo que con un dedo reconocido.
///
///   QR → registrar_acceso_qr (el servidor busca el código y le pregunta al juez de siempre,
///   registrar_acceso) → Puerta (aviso sonoro + torniquete), igual que la huella.
///
/// Corre separado del lector de huella: si el QR se desenchufa o falla, la huella sigue
/// marcando. Comparten sólo la Puerta, que atiende de a uno.
/// </summary>
public sealed class LectorQrService : BackgroundService
{
    private readonly AgentConfig _cfg;
    private readonly ConfigDelGimnasio _gym;
    private readonly HuellaRpc _rpc;
    private readonly Puerta _puerta;
    private readonly ILogger<LectorQrService> _log;

    // La lectura del puerto corre en su propio hilo y nunca espera al servidor: deja los
    // códigos acá y sigue escuchando. Si se juntan muchos (sin internet), se pierden los
    // más viejos, que ya no tienen a nadie esperando en la puerta.
    private readonly Channel<string> _codigos = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private string? _ultimoCodigo;
    private DateTime _ultimaVez = DateTime.MinValue;

    /// <summary>Para /health: <c>off</c>, <c>sin_lector</c>, <c>listo</c> o <c>error</c>.</summary>
    public string Estado { get; private set; } = "off";
    public string? Puerto { get; private set; }
    public string? Motivo { get; private set; }
    public DateTime? UltimaLecturaUtc { get; private set; }

    public LectorQrService(AgentConfig cfg, ConfigDelGimnasio gym, HuellaRpc rpc, Puerta puerta, ILogger<LectorQrService> log)
    {
        _cfg = cfg;
        _gym = gym;
        _rpc = rpc;
        _puerta = puerta;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_cfg.QrEnabled || !OperatingSystem.IsWindows())
        {
            (Estado, Motivo) = ("off", _cfg.QrEnabled ? "sólo en Windows" : "apagado en la configuración (QrEnabled)");
            return;
        }

        var leer = Task.Factory.StartNew(() => LeerSiempre(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await foreach (var codigo in _codigos.Reader.ReadAllAsync(ct))
            {
                try { await AtenderAsync(codigo, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.LogError(ex, "Lector QR: error al atender un código; sigue escuchando"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        try { await leer; } catch (OperationCanceledException) { }
    }

    private async Task AtenderAsync(string codigo, CancellationToken ct)
    {
        // Anti-rebote, como la huella: mientras el celular sigue frente al lector, éste lo
        // vuelve a leer, y cada lectura sería un pedido al servidor y un pitido.
        var ahora = DateTime.UtcNow;
        if (codigo == _ultimoCodigo && (ahora - _ultimaVez).TotalSeconds < _cfg.AutoDecideDedupSeconds) return;
        _ultimoCodigo = codigo;
        _ultimaVez = ahora;

        if (_rpc.TenantId is null || !_gym.Actual.AbreSinNavegador)
        {
            _log.LogWarning("Lector QR: leyó {Codigo} pero el agente no decide en esta PC ({Por})", Enmascarado(codigo),
                _rpc.TenantId is null ? "sin vincular" : "apagado desde el servidor");
            return;
        }

        _log.LogInformation("Lector QR: leyó {Codigo}", Enmascarado(codigo));
        var veredicto = await _rpc.RegistrarAccesoQrAsync(codigo, ct);
        await _puerta.AtenderAsync(veredicto, "qr", null, ct);
    }

    /// <summary>
    /// El código abre la puerta como una llave: al log (que viaja al servidor) va lo justo
    /// para reconocerlo, no entero. "FIT-6611-H7AE6TQV" → "FIT-6611-…(17)".
    /// </summary>
    public static string Enmascarado(string codigo) =>
        codigo.Length <= 8 ? $"…({codigo.Length})" : $"{codigo[..8]}…({codigo.Length})";

    // ── Lectura del puerto (hilo propio) ─────────────────────────────────────────

    private void LeerSiempre(CancellationToken ct)
    {
        string? avisado = null;   // para no repetir el mismo aviso en cada vuelta
        while (!ct.IsCancellationRequested)
        {
            PuertoQrElegido elegido;
            try { elegido = PuertoLectorQr.Elegir(_cfg.QrPort, PuertosSerieUsb.Listar(), SerialPort.GetPortNames()); }
            catch (Exception ex) { elegido = new PuertoQrElegido(null, $"no se pudo buscar el lector: {ex.Message}"); }

            if (elegido.Puerto is null)
            {
                if (avisado != elegido.Motivo) _log.LogInformation("Lector QR: {Motivo}", elegido.Motivo);
                avisado = elegido.Motivo;
                (Estado, Puerto, Motivo) = ("sin_lector", null, elegido.Motivo);
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
                continue;
            }

            try
            {
                Leer(elegido, ct);   // vuelve sólo al apagar el agente; si lo desenchufan, tira
                avisado = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var motivo = $"{elegido.Puerto}: {ex.Message}";
                if (avisado != motivo) _log.LogWarning("Lector QR: dejó de leer ({Motivo})", motivo);
                avisado = motivo;
                (Estado, Puerto, Motivo) = ("error", elegido.Puerto, motivo);
            }
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
        }
    }

    private void Leer(PuertoQrElegido elegido, CancellationToken ct)
    {
        var puerto = new SerialPort(elegido.Puerto!, 9600)
        {
            // Hay lectores USB que no mandan nada hasta ver DTR prendido: es la señal de
            // "hay un programa escuchando del otro lado".
            DtrEnable = true,
            RtsEnable = true,
            // Corto a propósito: cada 50 ms sin bytes se mira si hay un código sin cierre
            // para entregar y si hay que apagar.
            ReadTimeout = 50,
        };
        try
        {
            puerto.Open();
            _log.LogInformation("Lector QR: escuchando en {Puerto} — {Motivo}", elegido.Puerto, elegido.Motivo);
            (Estado, Puerto, Motivo) = ("listo", elegido.Puerto, elegido.Motivo);

            var codigos = new CodigosDelLector();
            var buf = new byte[256];
            while (!ct.IsCancellationRequested)
            {
                int n;
                try { n = puerto.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { n = 0; }
                var ahora = DateTime.UtcNow;
                foreach (var codigo in codigos.Agregar(buf, n, ahora)) Entregar(codigo);
                if (codigos.Vencido(ahora) is { } suelto) Entregar(suelto);
            }
        }
        finally
        {
            // Cerrar un puerto cuyo aparato ya no está puede tirar. Que no salga de acá: el
            // mismo proceso es el que lee la huella, y desenchufar el QR no puede tumbarla.
            try { puerto.Dispose(); } catch { }
        }
    }

    private void Entregar(string codigo)
    {
        UltimaLecturaUtc = DateTime.UtcNow;
        _codigos.Writer.TryWrite(codigo);
    }
}

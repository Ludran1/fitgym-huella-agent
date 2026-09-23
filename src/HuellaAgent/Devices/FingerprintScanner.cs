using HuellaAgent.Config;

namespace HuellaAgent.Devices;

/// <summary>Un dedo leido por el scanner, con el instante en que se leyo.</summary>
public sealed record ScanProbe(string Template, DateTime AtUtc);

/// <summary>
/// Mantiene el sensor leyendo SIEMPRE, en un loop de fondo, y bufferea el ultimo dedo
/// visto. /identify pasa a consumir ese buffer en vez de armar el sensor por request.
///
/// Por que existe
/// --------------
/// El modelo viejo era "capturar bajo demanda": cada POST /identify hacia DBInit +
/// N*DBAdd, abria una ventana de captura de 4s, y al responder desarmaba todo. Entre la
/// respuesta y el siguiente request el lector quedaba CIEGO: ~1.1-1.5s de cada ~5.2s en el
/// kiosko (POLL_MS=1000) = ~25% del tiempo. El socio que apoyaba el dedo justo en esa
/// ventana no era leido y tenia que reintentar, el "a veces agarra, a veces no": aleatorio
/// y parejo entre socios (no era calidad de template, eso falla siempre a los mismos).
/// Ademas AcquireFingerprint se llamaba cada 200ms, asi que un toque corto se escapaba
/// incluso con el sensor armado.
///
/// Ahora: el loop lee cada ScanPollMs (~40ms) sin parar y deja el dedo en un buffer de 1.
/// Un toque que ocurre mientras el frontend duerme o mientras el request viaja NO se
/// pierde: queda bufferado y se entrega en el siguiente poll. Zona muerta = 0.
///
/// Buffer de 1, consumo unico
/// --------------------------
/// El probe se entrega UNA sola vez y caduca a los ProbeFreshnessMs. Esto tambien mata el
/// bug de "asistencia fantasma" que el frontend parcheaba con su flag `armado`: al
/// reiniciar ya no hay un dedo viejo esperando para ser re-entregado.
/// </summary>
public sealed class FingerprintScanner : BackgroundService
{
    private readonly IFingerprintDevice _device;
    private readonly AgentConfig _cfg;
    private readonly ILogger<FingerprintScanner> _log;

    private readonly object _gate = new();
    private ScanProbe? _pending;
    private TaskCompletionSource _signal = NewSignal();

    /// <summary>
    /// Hasta cuando se considera que hay un ENROLADO en curso. Mientras dure, ningun dedo
    /// sale del scanner: ni al portero, ni a /identify.
    ///
    /// POR QUE NO ALCANZABA CON EL LOCK. Suspender el lector tapa la ventana de CADA
    /// /capture, pero un enrolado son tres capturas con huecos en el medio — y en esos
    /// huecos el dedo sigue sobre el vidrio. El caso concreto: el frontend descarta un
    /// apoyo repetido y espera 400 ms antes de volver a pedir. El loop, libre, lee ese
    /// mismo dedo y lo publica; el portero lo toma, le marca asistencia a la persona que
    /// esta siendo dada de alta y le abre la puerta en medio de su propio enrolado.
    ///
    /// Tambien cierra una carrera mas fina: `Publish()` corre FUERA del `_deviceLock` (se
    /// captura con el lock tomado y se publica despues de soltarlo), asi que un dedo leido
    /// justo antes de que entrara el enrolado podia caer en el buffer DESPUES de que
    /// `SuspendAsync` lo habia limpiado. Con esto, ese `Publish` tardio tampoco entra.
    ///
    /// Son ticks y no un DateTime por el `Interlocked`: lo escribe el hilo del request y
    /// lo lee el del scanner.
    /// </summary>
    private long _enrolandoHastaTicks;

    /// <summary>true = hay un enrolado en curso (o termino recien). Ver `_enrolandoHastaTicks`.</summary>
    public bool EnrolandoAhora => DateTime.UtcNow.Ticks < Interlocked.Read(ref _enrolandoHastaTicks);

    // Serializa el acceso al SDK entre el loop y el enrolado (/capture). El SDK nativo no
    // documenta thread-safety y ademas dos lectores compitiendo por el mismo dedo se roban
    // la captura entre si, asi que el enrolado pausa el loop mientras dura.
    private readonly SemaphoreSlim _deviceLock = new(1, 1);

    public FingerprintScanner(IFingerprintDevice device, AgentConfig cfg, ILogger<FingerprintScanner> log)
    {
        _device = device;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>true si el scanner esta activo (kill switch Agent:ContinuousScan).</summary>
    public bool Enabled => _cfg.ContinuousScan;

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cuando fue la ultima vez que el lector entrego un dedo (para /health).</summary>
    public DateTime? UltimaLecturaUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.ContinuousScan)
        {
            // Aun con el scanner apagado hay que mantener vivo el lector: sin este loop,
            // un lector que no abrio al arrancar no se reintenta nunca (el bug del 08 y
            // el 10-sep). Solo reconecta; los dedos los sigue pidiendo /identify.
            _log.LogInformation("Scanner continuo DESACTIVADO (Agent:ContinuousScan=false): /identify usa el camino legacy; solo se vigila la conexion del lector");
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_device.IsConnected) await ReconectarAsync(stoppingToken);
                await SafeDelay(2000, stoppingToken);
            }
            return;
        }

        _log.LogInformation("Scanner continuo activo (poll {Poll}ms, frescura {Fresh}ms)",
            _cfg.ScanPollMs, _cfg.ProbeFreshnessMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_device.IsConnected)
                {
                    // Lector cerrado (no abrio al arrancar, lo desenchufaron, lo tenia otro
                    // proceso): reintentar. El freno entre intentos lo pone el device.
                    await ReconectarAsync(stoppingToken);
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                CaptureResult? cap;
                await _deviceLock.WaitAsync(stoppingToken);
                try { cap = _device.TryCapture(); }
                finally { _deviceLock.Release(); }

                if (cap is null)
                {
                    await Task.Delay(_cfg.ScanPollMs, stoppingToken);   // sin dedo
                    continue;
                }

                UltimaLecturaUtc = DateTime.UtcNow;
                Publish(new ScanProbe(cap.Template, DateTime.UtcNow));

                // Debounce del dedo APOYADO: sin esto, mientras no lo levanta, el loop
                // republica el mismo dedo cada 40ms y machaca el buffer.
                await Task.Delay(_cfg.SameFingerDebounceMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (DeviceUnavailableException ex)
            {
                _log.LogWarning("Scanner: lector no disponible ({Msg}); reintenta en 2s", ex.Message);
                await SafeDelay(2000, stoppingToken);
            }
            catch (Exception ex)
            {
                // Nunca dejar morir el loop: si muere, el gym deja de leer huellas hasta
                // reiniciar el agente y nadie se entera (el agente corre oculto).
                _log.LogError(ex, "Scanner: error inesperado en el loop; reintenta en 2s");
                await SafeDelay(2000, stoppingToken);
            }
        }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    /// <summary>Reabre el lector, sin pisarse con una captura en curso.</summary>
    private async Task ReconectarAsync(CancellationToken ct)
    {
        try
        {
            await _deviceLock.WaitAsync(ct);
            try { _device.TryReconnect(); }
            finally { _deviceLock.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Scanner: fallo el intento de reconectar"); }
    }

    private void Publish(ScanProbe probe)
    {
        // El dedo de un enrolado NO es alguien entrando al gimnasio: es la misma persona
        // que esta siendo dada de alta, del otro lado del mostrador. Se descarta acá, en el
        // unico punto por donde un dedo entra al buffer.
        if (EnrolandoAhora) return;

        TaskCompletionSource toWake;
        lock (_gate)
        {
            _pending = probe;
            toWake = _signal;
            _signal = NewSignal();
        }
        toWake.TrySetResult();   // despierta a los /identify que esperan
    }

    /// <summary>
    /// Espera hasta <paramref name="timeoutSeconds"/> un dedo fresco y lo CONSUME.
    /// null = no hubo dedo en la ventana (=&gt; 408). Un dedo leido mientras el frontend
    /// dormia ya esta en el buffer y vuelve inmediatamente.
    /// </summary>
    public async Task<ScanProbe?> WaitForProbeAsync(int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_pending is { } p)
                {
                    // Solo sirve si es reciente: un dedo de hace 10s no debe marcar ahora.
                    _pending = null;
                    if ((DateTime.UtcNow - p.AtUtc).TotalMilliseconds <= _cfg.ProbeFreshnessMs)
                        return p;
                }
                wait = _signal.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;

            // Despierta con el proximo dedo o al vencer la ventana, lo que pase primero.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(remaining, timeoutCts.Token);
            var done = await Task.WhenAny(wait, delay);
            timeoutCts.Cancel();   // corta el Task.Delay pendiente (no acumular timers)
            if (done == delay)
            {
                ct.ThrowIfCancellationRequested();   // cancelado por el cliente, no por timeout
                return null;
            }
        }
    }

    /// <summary>
    /// Toma el lector en exclusiva (pausa el loop) mientras dure el using. Lo usa /capture
    /// durante el enrolado: el scanner y el enrolado no pueden competir por el mismo dedo.
    /// </summary>
    public async Task<IDisposable> SuspendAsync(CancellationToken ct)
    {
        // La marca va ANTES del lock, no despues: esperar el semaforo puede tardar lo que
        // dure una vuelta del loop, y en ese rato un dedo ya leido podria publicarse.
        Interlocked.Exchange(ref _enrolandoHastaTicks, DateTime.MaxValue.Ticks);
        try
        {
            await _deviceLock.WaitAsync(ct);
        }
        catch
        {
            // Cancelado esperando el lector: hay que soltar la marca o el portero queda
            // mudo para siempre por un enrolado que nunca llego a empezar.
            Interlocked.Exchange(ref _enrolandoHastaTicks, VenceEn(_cfg.EnroladoGraciaMs));
            throw;
        }
        // El buffer puede tener un dedo del enrolado en curso; que no lo consuma un
        // /identify y marque asistencia en medio del alta de un socio.
        lock (_gate) { _pending = null; }
        return new Release(_deviceLock, this);
    }

    private static long VenceEn(int ms) => DateTime.UtcNow.AddMilliseconds(Math.Max(0, ms)).Ticks;

    private sealed class Release : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private readonly FingerprintScanner _owner;
        private int _done;
        public Release(SemaphoreSlim sem, FingerprintScanner owner) { _sem = sem; _owner = owner; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (_owner._gate) { _owner._pending = null; }   // descarta el dedo del enrolado
            // La gracia cubre el hueco ENTRE capturas: el enrolado son tres, y entre una y
            // otra el dedo sigue apoyado. Sin esto, el portero se lo lleva en ese hueco.
            Interlocked.Exchange(ref _owner._enrolandoHastaTicks, VenceEn(_owner._cfg.EnroladoGraciaMs));
            _sem.Release();
        }
    }
}

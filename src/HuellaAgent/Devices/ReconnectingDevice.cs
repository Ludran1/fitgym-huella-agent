using HuellaAgent.Config;
using HuellaAgent.Storage;

namespace HuellaAgent.Devices;

/// <summary>
/// Envuelve al lector real y se encarga de ABRIRLO Y REABRIRLO. El agente v1.0.2 abria el
/// lector una sola vez, en el constructor, y si fallaba caia al MockDevice para siempre:
/// el 08 y el 10-sep arranco con `zkfp2.Init fallo` al iniciar sesion (el USB todavia no
/// estaba listo) y se paso el dia entero sin leer un solo dedo, mientras /health decia
/// reader "connected".
///
/// Aca el lector cerrado es un estado normal, no el fin:
/// - se reintenta abrir cada ReconnectSeconds (lo dispara el scanner, que ya tiene su loop);
/// - si una lectura falla porque el lector se fue, se cierra y se vuelve a la cola de
///   reintentos en vez de seguir hablandole a un handle muerto;
/// - /health dice la verdad: `reader`, el modelo real y el ultimo error.
///
/// Nunca cae a MockDevice: un lector simulado en produccion es peor que uno ausente,
/// porque nadie se entera.
/// </summary>
public sealed class ReconnectingDevice : IFingerprintDevice, IDisposable
{
    private readonly Func<IFingerprintDevice> _abrir;
    private readonly ILogger _log;
    private readonly TimeSpan _espera;
    private readonly object _gate = new();

    private IFingerprintDevice? _lector;
    private DateTime _ultimoIntento = DateTime.MinValue;
    private int _fallosSeguidos;

    public ReconnectingDevice(Func<IFingerprintDevice> abrir, AgentConfig cfg, ILogger log)
    {
        _abrir = abrir;
        _log = log;
        _espera = TimeSpan.FromSeconds(Math.Max(1, cfg.ReconnectSeconds));
    }

    public bool IsConnected { get { lock (_gate) return _lector?.IsConnected == true; } }
    public string? DeviceName { get { lock (_gate) return _lector?.DeviceName ?? "(sin lector)"; } }
    public DateTime? ConnectedSinceUtc { get { lock (_gate) return _lector?.ConnectedSinceUtc; } }
    public string? LastError { get; private set; }

    /// <summary>Intentos fallidos seguidos de abrir el lector (0 = esta abierto).</summary>
    public int FallosSeguidos { get { lock (_gate) return _fallosSeguidos; } }

    public bool TryReconnect()
    {
        lock (_gate)
        {
            if (_lector?.IsConnected == true) return true;
            if (DateTime.UtcNow - _ultimoIntento < _espera) return false;   // freno entre intentos
            _ultimoIntento = DateTime.UtcNow;

            Cerrar();
            try
            {
                _lector = _abrir();
                _fallosSeguidos = 0;
                LastError = null;
                _log.LogInformation("Lector abierto: {Device}", _lector.DeviceName);
                return true;
            }
            catch (Exception ex)
            {
                _fallosSeguidos++;
                LastError = ex.Message;
                // Ruidoso la primera vez y despues cada ~1 min: si el lector no esta, el
                // log no tiene que taparse con una linea cada 2 segundos.
                if (_fallosSeguidos == 1 || _fallosSeguidos % 30 == 0)
                    _log.LogWarning("No se pudo abrir el lector ({Intento} intentos): {Msg}", _fallosSeguidos, ex.Message);
                return false;
            }
        }
    }

    // Una lectura fallo porque el lector se fue: soltarlo para que el proximo TryReconnect
    // rehaga el ciclo completo (CloseDevice - Terminate - Init - OpenDevice).
    private void Perdido(DeviceUnavailableException ex)
    {
        lock (_gate)
        {
            if (_lector is null) return;
            LastError = ex.Message;
            _log.LogWarning("Se perdio el lector ({Msg}); reintentando cada {Seg}s", ex.Message, _espera.TotalSeconds);
            Cerrar();
        }
    }

    private void Cerrar()
    {
        if (_lector is IDisposable d)
        {
            try { d.Dispose(); } catch (Exception ex) { _log.LogDebug(ex, "Error cerrando el lector"); }
        }
        _lector = null;
    }

    private IFingerprintDevice Activo()
    {
        lock (_gate)
        {
            if (_lector?.IsConnected == true) return _lector;
        }
        throw new DeviceUnavailableException(LastError ?? "lector desconectado");
    }

    public CaptureResult? TryCapture()
    {
        IFingerprintDevice lector;
        lock (_gate)
        {
            if (_lector?.IsConnected != true) return null;   // el scanner ya reintenta abrir
            lector = _lector;
        }
        try { return lector.TryCapture(); }
        catch (DeviceUnavailableException ex) { Perdido(ex); return null; }
    }

    public async Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
    {
        var lector = Activo();
        try { return await lector.CaptureAsync(timeoutSeconds, ct); }
        catch (DeviceUnavailableException ex) { Perdido(ex); throw; }
    }

    public string Merge(string t1, string t2, string t3)
    {
        var lector = Activo();
        try { return lector.Merge(t1, t2, t3); }
        catch (DeviceUnavailableException ex) { Perdido(ex); throw; }
    }

    public int Match(string t1, string t2)
    {
        var lector = Activo();
        try { return lector.Match(t1, t2); }
        catch (DeviceUnavailableException ex) { Perdido(ex); throw; }
    }

    public IdentifyMatch? Identify(string probeTemplate, IReadOnlyList<StoredTemplate> db, string dbKey)
    {
        var lector = Activo();
        try
        {
            var match = lector.Identify(probeTemplate, db, dbKey);
            LastError = lector.LastError;   // p.ej. templates que no entraron en la base
            return match;
        }
        catch (DeviceUnavailableException ex) { Perdido(ex); throw; }
    }

    public async Task<IdentifyMatch?> IdentifyAsync(IReadOnlyList<StoredTemplate> db, int timeoutSeconds, CancellationToken ct)
    {
        var lector = Activo();
        try { return await lector.IdentifyAsync(db, timeoutSeconds, ct); }
        catch (DeviceUnavailableException ex) { Perdido(ex); throw; }
    }

    public void Dispose() { lock (_gate) Cerrar(); }
}

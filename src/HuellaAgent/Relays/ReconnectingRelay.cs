using HuellaAgent.Config;

namespace HuellaAgent.Relays;

/// <summary>
/// Envuelve al rele USB real y se encarga de abrir el COM cuando hace falta.
///
/// Antes, si el COM fallaba al arrancar (el rele desenchufado, el cable flojo, el puerto
/// tomado por el kiosko), RelayFactory caia a MockRelay para siempre: el agente seguia
/// contestando `ok` en cada apertura y NADIE se enteraba de que la puerta no abria. Es el
/// mismo bug que el MockDevice del lector, en la pieza que mueve el fierro.
///
/// Aca el COM se abre al primer pulso y se reintenta en los siguientes: enchufar el rele
/// con el agente ya corriendo alcanza, sin reiniciar nada. Si no se puede abrir, el pulso
/// FALLA con el motivo (el endpoint lo devuelve como 503) y /health lo muestra.
/// </summary>
public sealed class ReconnectingRelay : IRelay, IDisposable
{
    private readonly Func<IRelay> _abrir;
    private readonly ILogger _log;
    private readonly string _puerto;
    private readonly object _gate = new();
    private IRelay? _rele;

    public ReconnectingRelay(Func<IRelay> abrir, AgentConfig cfg, ILogger log)
    {
        _abrir = abrir;
        _log = log;
        _puerto = cfg.RelayPort;
        Intentar();   // primer intento al arrancar, para que /health diga algo util
    }

    public bool IsConnected { get { lock (_gate) return _rele?.IsConnected == true; } }
    public string? LastError { get; private set; }

    private bool Intentar()
    {
        lock (_gate)
        {
            if (_rele?.IsConnected == true) return true;
            Cerrar();
            try
            {
                _rele = _abrir();
                LastError = null;
                _log.LogInformation("Rele abierto en {Puerto}", _puerto);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"{_puerto}: {ex.Message}";
                _log.LogWarning("No se pudo abrir el rele en {Puerto}: {Msg}", _puerto, ex.Message);
                return false;
            }
        }
    }

    private void Cerrar()
    {
        if (_rele is IDisposable d)
        {
            try { d.Dispose(); } catch (Exception ex) { _log.LogDebug(ex, "Error cerrando el rele"); }
        }
        _rele = null;
    }

    public async Task PulseAsync(int pulseMs, CancellationToken ct)
    {
        IRelay rele;
        lock (_gate)
        {
            if (_rele?.IsConnected != true && !Intentar())
                throw new InvalidOperationException(LastError ?? $"el rele no responde en {_puerto}");
            rele = _rele!;
        }

        try { await rele.PulseAsync(pulseMs, ct); }
        catch (Exception ex)
        {
            // Se desenchufo con el agente corriendo: soltarlo para reabrirlo en el proximo
            // pulso, y que este falle con el motivo en vez de darse por bueno.
            lock (_gate) { LastError = $"{_puerto}: {ex.Message}"; Cerrar(); }
            throw;
        }
    }

    public void Dispose() { lock (_gate) Cerrar(); }
}

using HuellaAgent.Config;
using HuellaAgent.Relays;
using HuellaAgent.Storage;
using HuellaAgent.Supabase;

namespace HuellaAgent.Devices;

/// <summary>
/// El latido: le cuenta al servidor cómo está este lector, y se baja la configuración del
/// gimnasio.
///
/// POR QUÉ. Hasta hoy, desde el panel de super-admin NO SE VEÍA NADA de un lector.
/// `/health` y `/logs` son localhost: sólo los ve un navegador abierto en esa misma PC.
/// Las cuatro RPC que usaba el agente son acciones, ninguna reporta estado. Entonces un
/// gimnasio instalaba, el lector no abría por el driver, **ellos no llamaban —no sabían que
/// debería andar— y nadie se enteraba en tres semanas**. Ese es el modo de falla que hace
/// imposible que un gimnasio se instale solo: no es que se rompa, es que se rompe callado.
///
/// LO QUE ESTE SERVICIO NO PUEDE HACER, y por eso está separado del portero:
///
///   · NO toca el lector. Lee `IsConnected`, el conteo y la última lectura; nada de eso
///     pide el `_deviceLock`. No puede robarle un dedo al portero ni demorar una apertura.
///   · NO vuelve a probar el relé. `/health` llama a `TryConnect()`, que si el relé está
///     cerrado intenta abrir el puerto serie. Acá se lee el último estado conocido: golpear
///     el hardware cada pocos minutos, para siempre, no vale lo que cuesta.
///   · NO puede hacer fallar nada. Si el servidor no contesta, se saltea y reintenta. El
///     latido es para MIRAR, no para funcionar.
///
/// Costo: un POST de ~1 KB cada `LatidoMinutos`. El panel del dueño ya pide `/health` cada
/// cinco SEGUNDOS mientras está abierto; esto es dos órdenes de magnitud menos.
/// </summary>
public sealed class LatidoService : BackgroundService
{
    private readonly HuellaRpc _rpc;
    private readonly IFingerprintDevice _device;
    private readonly ITemplateStore _store;
    private readonly FingerprintScanner _scanner;
    private readonly IRelay _relay;
    private readonly ConfigDelGimnasio _gym;
    private readonly AgentConfig _cfg;
    private readonly ILogger<LatidoService> _log;

    public LatidoService(HuellaRpc rpc, IFingerprintDevice device, ITemplateStore store,
                         FingerprintScanner scanner, IRelay relay, ConfigDelGimnasio gym,
                         AgentConfig cfg, ILogger<LatidoService> log)
    {
        _rpc = rpc; _device = device; _store = store; _scanner = scanner;
        _relay = relay; _gym = gym; _cfg = cfg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_cfg.LatidoMinutos <= 0)
        {
            _log.LogInformation("Latido DESACTIVADO (Agent:LatidoMinutos <= 0)");
            return;
        }

        var espera = TimeSpan.FromMinutes(_cfg.LatidoMinutos);
        _log.LogInformation("Latido activo (cada {Min} min)", _cfg.LatidoMinutos);

        // Un rato antes del primer latido: al arrancar, el lector puede estar todavía
        // abriéndose y reportaríamos "desconectado" en cada reinicio.
        await Dormir(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UnaVuelta(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Nunca dejar morir el loop: si muere, el gimnasio desaparece del panel y
                // se ve igual que un agente caído — la falsa alarma que esto viene a evitar.
                _log.LogWarning(ex, "Latido: error inesperado");
            }
            await Dormir(espera, stoppingToken);
        }
    }

    private async Task UnaVuelta(CancellationToken ct)
    {
        if (!_rpc.Enabled) return;   // sin vincular no hay a quién contarle

        // ── 1. Bajar la configuración del gimnasio ───────────────────────────────
        // Se pide en cada vuelta y no sólo al vincular: así el dueño prende el torniquete
        // desde el panel y el agente se entera solo, sin que nadie vuelva a esa PC.
        var config = await _rpc.ConfigDelGimnasioAsync(ct);
        if (config is not null) _gym.AplicarDelServidor(config);
        // null = no se pudo preguntar, o el gimnasio todavía no configuró nada. En los dos
        // casos NO se toca nada: el agente se queda con lo último que sabía. Ni un corte de
        // internet ni una tabla vacía pueden apagarle la puerta a un gimnasio.

        // ── 2. Contar cómo estamos ───────────────────────────────────────────────
        var vigente = _gym.Actual;
        var ok = await _rpc.LatidoAsync(Environment.MachineName, new
        {
            version = typeof(LatidoService).Assembly.GetName().Version?.ToString(3),
            lector_conectado = _device.IsConnected,
            dispositivo = _device.DeviceName,
            huellas_cargadas = await _store.CountAsync(),
            decide = vigente.AbreSinNavegador && _scanner.Enabled ? "agente" : "navegador",
            umbral = vigente.Umbral,
            // El ÚLTIMO estado conocido del relé, sin volver a abrir el puerto.
            torniquete = !vigente.TieneTorniquete ? "off" : _relay.IsConnected ? "ready" : "error",
            ultimo_dedo_en = _scanner.UltimaLecturaUtc?.ToString("o"),
            ultimo_error = _device.LastError ?? _relay.LastError,
            boot_id = Api.FingerprintEndpoints.BootId,
        }, ct);

        if (!ok) _log.LogDebug("Latido: el servidor no lo tomó (sigue andando igual)");
    }

    private static async Task Dormir(TimeSpan t, CancellationToken ct)
    {
        try { await Task.Delay(t, ct); } catch (OperationCanceledException) { }
    }
}

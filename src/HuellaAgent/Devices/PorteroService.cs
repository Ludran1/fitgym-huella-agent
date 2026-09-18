using HuellaAgent.Config;
using HuellaAgent.Relays;
using HuellaAgent.Storage;
using HuellaAgent.Supabase;

namespace HuellaAgent.Devices;

/// <summary>
/// El PORTERO: el agente decide y abre por su cuenta, sin navegador.
///
/// Hasta ahora la cadena era: el agente reconoce el dedo → el navegador pregunta, decide y
/// le pide al agente que pulse el relé. Con Chrome cerrado no entraba nadie, aunque el
/// lector y el agente estuvieran perfectos — verificado en campo el 18-sep.
///
/// Ahora, con `Agent:AutoDecide`, este loop hace el camino completo:
///   dedo → 1:N local → registrar_acceso (el juez único en Supabase) → pulso al relé.
///
/// Quién decide sigue SIN ser el agente: la vigencia, el cupo y el "ya entró hoy" los
/// resuelve la RPC. El agente reconoce, pregunta y obedece. Por eso no hay reglas de
/// acceso duplicadas acá.
///
/// Mientras esté prendido, el navegador NO debe pedir identify: /health anuncia
/// `decide: "agente"` y el listener del panel se apaga solo. Si los dos preguntaran, se
/// robarían el dedo y la puerta abriría dos veces.
/// </summary>
public sealed class PorteroService : BackgroundService
{
    private readonly FingerprintScanner _scanner;
    private readonly Bocina _bocina;
    private readonly IFingerprintDevice _device;
    private readonly ITemplateStore _store;
    private readonly HuellaRpc _rpc;
    private readonly IRelay _relay;
    private readonly AgentConfig _cfg;
    private readonly ILogger<PorteroService> _log;

    // Anti-rebote: el mismo dedo apoyado no vuelve a pedir veredicto hasta pasada la
    // ventana. Sin esto, un dedo que queda apoyado dispara una consulta por segundo.
    private string? _ultimaPersona;
    private DateTime _ultimaVez = DateTime.MinValue;

    public PorteroService(FingerprintScanner scanner, IFingerprintDevice device, ITemplateStore store,
                          HuellaRpc rpc, IRelay relay, Bocina bocina, AgentConfig cfg, ILogger<PorteroService> log)
    {
        _scanner = scanner;
        _bocina = bocina;
        _device = device;
        _store = store;
        _rpc = rpc;
        _relay = relay;
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.AutoDecide)
        {
            _log.LogInformation("Portero DESACTIVADO (Agent:AutoDecide=false): decide el navegador, como hasta ahora");
            return;
        }
        if (!_scanner.Enabled)
        {
            _log.LogWarning("Portero: necesita el scanner continuo (Agent:ContinuousScan=true). No arranca.");
            return;
        }

        _log.LogInformation("Portero ACTIVO: el agente decide y abre sin navegador (rebote {Seg}s)",
            _cfg.AutoDecideDedupSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tenant = _rpc.TenantId;
                if (tenant is null)
                {
                    // Sin vincular no hay a quién preguntarle: el lector no sabe de qué gym es.
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                var probe = await _scanner.WaitForProbeAsync(_cfg.IdentifyTimeoutSeconds, stoppingToken);
                if (probe is null) continue;   // nadie apoyó el dedo en la ventana

                var db = await _store.LoadAsync(tenant);
                var match = _device.Identify(probe.Template, db, $"{tenant}:{_store.Version}");
                if (match is null)
                {
                    _log.LogInformation("Portero: dedo sin coincidencia ({Cuantas} huellas cargadas)", db.Count);
                    _bocina.Rechazo();   // nadie mas le va a avisar que no lo reconocio
                    continue;
                }

                var entry = db.FirstOrDefault(t => t.Uid == match.Uid);
                if (entry is null) continue;   // uid huerfano en el cache

                if (EsRebote(entry.ClienteId)) continue;

                var veredicto = await _rpc.RegistrarAccesoAsync(entry.ClienteId, "huella", match.Score, stoppingToken);
                if (veredicto is null)
                {
                    // Sin veredicto no se abre. Es la decisión correcta mientras no exista la
                    // copia local de vigencias: preferimos que el socio pase por recepción a
                    // abrirle la puerta a cualquiera porque se cayó internet.
                    _log.LogWarning("Portero: sin respuesta del servidor; NO se abre (score {Score})", match.Score);
                    continue;
                }

                if (!veredicto.AbrirPuerta)
                {
                    _log.LogInformation("Portero: {Nombre} NO pasa ({Motivo}) score={Score}",
                        veredicto.Nombre ?? "(desconocido)", veredicto.Motivo, match.Score);
                    _bocina.Rechazo();
                    continue;
                }

                _log.LogInformation("Portero: pasa {Nombre} ({Tipo}{YaHoy}) score={Score}",
                    veredicto.Nombre, veredicto.Tipo, veredicto.YaHoy ? ", ya habia entrado hoy" : "", match.Score);
                _bocina.Ok();

                if (!_cfg.TurnstileEnabled) continue;   // gym sin torniquete: solo se registra
                try
                {
                    await _relay.PulseAsync(_cfg.RelayPulseMs, stoppingToken);
                }
                catch (Exception ex)
                {
                    // La asistencia YA quedó registrada: que la puerta falle no debe borrarla.
                    _log.LogError(ex, "Portero: el acceso se concedio pero el rele no abrio");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Portero: error inesperado; reintenta en 2s");
                try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private bool EsRebote(string personaId)
    {
        var ahora = DateTime.UtcNow;
        if (_ultimaPersona == personaId && (ahora - _ultimaVez).TotalSeconds < _cfg.AutoDecideDedupSeconds)
            return true;
        _ultimaPersona = personaId;
        _ultimaVez = ahora;
        return false;
    }
}

using HuellaAgent.Config;

namespace HuellaAgent.Relays;

/// <summary>
/// Rele real (envuelto en ReconnectingRelay) en Windows + TurnstileEnabled + RelayPort; en
/// cualquier otro caso MockRelay, que es solo para dev y para los gyms SIN torniquete.
///
/// ⚠️ Ya NO cae a MockRelay cuando el COM falla. Antes si: el agente se quedaba con un rele
/// de mentira que contestaba `ok` a cada apertura, asi que la puerta no abria y en la app
/// todo se veia bien. Detectado el 17-sep en la PC de Adriano, donde el COM3 configurado no
/// existe (el rele no estaba enchufado) y /health igual decia torniquete "ready".
/// </summary>
public static class RelayFactory
{
    public static IRelay Create(AgentConfig cfg, ILoggerFactory lf)
    {
        var log = lf.CreateLogger("RelayFactory");

        if (!OperatingSystem.IsWindows() || !cfg.TurnstileEnabled)
            return new MockRelay(lf.CreateLogger<MockRelay>());

        // El puerto ya no hay que ir a buscarlo al Administrador de dispositivos: si no
        // esta configurado, se detecta. Era el ultimo dato de la instalacion que obligaba
        // a que alguien fuera hasta la PC (ver PuertoRele y HU-17 del PRD 103).
        var elegido = PuertoRele.Elegir(cfg.RelayPort);
        if (elegido.Port is null)
        {
            // Sin puerto NO se cae al MockRelay: ese fue el bug del 17-sep, un rele de
            // mentira contestando ok a cada apertura mientras la puerta no abria. Se
            // devuelve el rele real igual, que va a fallar al conectar y lo va a DECIR en
            // /health con este motivo al lado.
            log.LogWarning("Torniquete: no se pudo determinar el puerto del rele ({Motivo})", elegido.Motivo);
            return new ReconnectingRelay(
                () => throw new InvalidOperationException($"no hay puerto para el rele: {elegido.Motivo}"),
                cfg, log);
        }

        log.LogInformation("Torniquete: usando {Port} — {Motivo}", elegido.Port, elegido.Motivo);
        return new ReconnectingRelay(
            () => new UsbRelay(elegido.Port, lf.CreateLogger<UsbRelay>()), cfg, log);
    }
}

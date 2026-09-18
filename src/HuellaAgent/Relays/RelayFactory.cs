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
        if (OperatingSystem.IsWindows() && cfg.TurnstileEnabled && !string.IsNullOrWhiteSpace(cfg.RelayPort))
            return new ReconnectingRelay(
                () => new UsbRelay(cfg, lf.CreateLogger<UsbRelay>()), cfg, lf.CreateLogger("RelayFactory"));

        return new MockRelay(lf.CreateLogger<MockRelay>());
    }
}

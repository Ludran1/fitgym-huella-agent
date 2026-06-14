using HuellaAgent.Config;

namespace HuellaAgent.Relays;

/// <summary>
/// UsbRelay solo en Windows + TurnstileEnabled + RelayPort seteado; si no, MockRelay.
/// Si el COM falla al abrir, cae a MockRelay (no tumba el agente: la huella sigue marcando
/// asistencia aunque el torniquete no responda).
/// </summary>
public static class RelayFactory
{
    public static IRelay Create(AgentConfig cfg, ILoggerFactory lf)
    {
        if (OperatingSystem.IsWindows() && cfg.TurnstileEnabled && !string.IsNullOrWhiteSpace(cfg.RelayPort))
        {
            try { return new UsbRelay(cfg, lf.CreateLogger<UsbRelay>()); }
            catch (Exception ex)
            {
                lf.CreateLogger("RelayFactory").LogWarning(ex, "USB-rele no disponible; usando MockRelay");
            }
        }
        return new MockRelay(lf.CreateLogger<MockRelay>());
    }
}

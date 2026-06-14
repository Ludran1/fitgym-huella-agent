using HuellaAgent.Config;

namespace HuellaAgent.Devices;

/// <summary>
/// Elige la implementacion del lector: ZkfpDevice solo en Windows + UseRealDevice=true;
/// en cualquier otro caso (Linux, CI, dev sin flag) MockDevice. Si el SDK real falla al
/// abrir (lector desenchufado), cae a Mock para que /health responda "disconnected" en vez
/// de tumbar el proceso.
/// </summary>
public static class DeviceFactory
{
    public static IFingerprintDevice Create(AgentConfig cfg, ILogger? log = null)
    {
#if ZKFP
        // ZkfpDevice solo existe en builds win-x64 (define ZKFP). En Linux/CI no se compila.
        if (OperatingSystem.IsWindows() && cfg.UseRealDevice)
        {
            try { return new ZkfpDevice(cfg); }
            catch (Exception ex) { log?.LogWarning(ex, "SDK real no disponible; usando MockDevice"); }
        }
#endif
        return new MockDevice();
    }
}

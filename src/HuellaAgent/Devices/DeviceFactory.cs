using HuellaAgent.Config;

namespace HuellaAgent.Devices;

/// <summary>
/// Elige la implementacion del lector: el real (envuelto en ReconnectingDevice) en Windows
/// con UseRealDevice=true; en cualquier otro caso (Linux, CI, dev sin flag) MockDevice.
///
/// ⚠️ Ya NO cae al MockDevice cuando el SDK real falla. Esa "red de seguridad" era el peor
/// bug de campo del agente: el 08 y el 10-sep el lector no abrio al iniciar sesion, el
/// agente siguio vivo con un lector simulado y /health reporto reader "connected" todo el
/// dia. Ahora, si el lector no abre, se reintenta para siempre y /health lo dice.
/// </summary>
public static class DeviceFactory
{
    public static IFingerprintDevice Create(AgentConfig cfg, ILogger? log = null)
    {
        var logger = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
#if ZKFP
        // ZkfpDevice solo existe en builds win-x64 (define ZKFP). En Linux/CI no se compila.
        if (OperatingSystem.IsWindows() && cfg.UseRealDevice)
        {
            var real = new ReconnectingDevice(() => new ZkfpDevice(cfg), cfg, logger);
            real.TryReconnect();   // primer intento ya, para que /health diga algo util
            return real;
        }

        // Un ZKTeco enchufado + UseRealDevice=false es casi siempre un accidente, no una
        // eleccion: el appsettings.json no se encontro, o quedo el de un build de dev. Antes
        // esto era invisible —/health decia reader "connected" sobre un lector inventado— y
        // el gimnasio se enteraba cuando a nadie le abria la puerta. Que grite.
        if (OperatingSystem.IsWindows() && UsbPresencia.HayLector())
            logger.LogWarning("Hay un lector ZKTeco enchufado pero UseRealDevice=false: va MockDevice y NADIE va a poder entrar. Revisar que appsettings.json este junto al exe.");
#else
        if (cfg.UseRealDevice)
            logger.LogWarning("UseRealDevice=true pero este build no trae el SDK real (falta publicar con -r win-x64): va MockDevice.");
#endif

        if (cfg.ContinuousScan && cfg.MockAutoFinger)
            logger.LogWarning("MockDevice con MockAutoFinger=true y scanner continuo: va a inventar dedos cada 2s. Solo para dev.");
        return new MockDevice(cfg);
    }
}

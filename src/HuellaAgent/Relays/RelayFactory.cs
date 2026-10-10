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
        return CrearReal(cfg, lf);
    }

    /// <summary>
    /// El relé real, sin preguntar si el gimnasio tiene torniquete: eso lo decide quien lo
    /// usa. Lo llama ReleSegunGimnasio la primera vez que la config del gym dice que sí.
    ///
    /// El puerto se elige en CADA intento de abrir, no una sola vez al crearlo. Hasta la
    /// v1.7.1 se elegía acá arriba y quedaba fijo adentro del reintento: si el relé no estaba
    /// enchufado cuando arrancó el agente, cada intento repetía "no hay ningún puerto serie"
    /// aunque Windows ya mostrara el CH340 en COM4 —visto el 09-oct en la PC de Adriano—, y
    /// la promesa de ReconnectingRelay ("enchufarlo con el agente corriendo alcanza") no se
    /// cumplía. Lo mismo si Windows le cambia el número al reenchufarlo.
    /// </summary>
    /// <param name="elegir">Para los tests: qué puerto elige la detección. Por defecto, PuertoRele.</param>
    /// <param name="crear">Para los tests: cómo se abre el relé en un puerto. Por defecto, UsbRelay.</param>
    public static IRelay CrearReal(AgentConfig cfg, ILoggerFactory lf,
                                   Func<PuertoElegido>? elegir = null, Func<string, IRelay>? crear = null)
    {
        var log = lf.CreateLogger("RelayFactory");
        // El puerto ya no hay que ir a buscarlo al Administrador de dispositivos: si no
        // esta configurado, se detecta. Era el ultimo dato de la instalacion que obligaba
        // a que alguien fuera hasta la PC (ver PuertoRele y HU-17 del PRD 103).
        //
        // Sin los puertos del lector QR (v1.8): con el lector en modo COM hay dos puertos
        // serie, y la regla "el único puerto serie es el relé" agarraba el del lector.
        elegir ??= () => PuertoRele.Elegir(cfg.RelayPort, () => Qr.PuertoLectorQr.SinLosDelLector(
            System.IO.Ports.SerialPort.GetPortNames(), Qr.PuertosSerieUsb.Listar(), cfg.QrPort));
        crear ??= port => new UsbRelay(port, lf.CreateLogger<UsbRelay>());
        string? ultimoMotivo = null;   // para no repetir el mismo aviso en cada reintento

        return new ReconnectingRelay(() =>
        {
            var elegido = elegir();
            if (elegido.Port is null)
            {
                // Sin puerto NO se cae al MockRelay: ese fue el bug del 17-sep, un rele de
                // mentira contestando ok a cada apertura mientras la puerta no abria. Falla,
                // y lo DICE en /health con este motivo.
                if (ultimoMotivo != elegido.Motivo)
                    log.LogWarning("Torniquete: no se pudo determinar el puerto del rele ({Motivo})", elegido.Motivo);
                ultimoMotivo = elegido.Motivo;
                throw new InvalidOperationException($"no hay puerto para el relé: {elegido.Motivo}");
            }
            if (ultimoMotivo != elegido.Motivo)
                log.LogInformation("Torniquete: usando {Port} — {Motivo}", elegido.Port, elegido.Motivo);
            ultimoMotivo = elegido.Motivo;
            return crear(elegido.Port);
        }, cfg, log);
    }
}

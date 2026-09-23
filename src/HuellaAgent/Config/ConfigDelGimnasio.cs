namespace HuellaAgent.Config;

/// <summary>Lo que es del GIMNASIO, no de esta computadora.</summary>
public sealed record ConfigGym(bool AbreSinNavegador, bool TieneTorniquete, int PulsoMs, int Umbral);

/// <summary>
/// La configuración vigente del gimnasio, que puede venir del servidor o del archivo.
///
/// POR QUÉ EXISTE. Hasta el 23-sep, si la puerta abría sin navegador, si había torniquete y
/// cuánto duraba el pulso vivían en `appsettings.json`, en ESA computadora. El día que el
/// gimnasio cambia de PC hay que reproducirlos a mano, con un editor de texto. Un
/// recepcionista no hace eso: cada mudanza terminaba en una llamada.
///
/// Ahora bajan con el vínculo (`huella_config`), igual que ya bajaban las huellas. Mudarse
/// pasa a ser instalar y un clic.
///
/// TRES REGLAS QUE NO SE NEGOCIAN:
///
///   · El archivo es el PISO, no el techo. Se arranca con lo que dice `appsettings.json`
///     y el servidor lo pisa cuando contesta. Un agente contra una base sin la migración
///     sigue funcionando exactamente como antes.
///   · Si el servidor NO contesta, se queda con lo último que sabía. Un corte de internet
///     no puede apagarle la puerta a un gimnasio — es la misma decisión que ya se tomó con
///     el portero, que sin veredicto no abre pero tampoco se rompe.
///   · El puerto COM no entra acá. Es lo único que de verdad cambia entre computadoras, y
///     mandarlo desde el servidor sería reproducir el problema que esto viene a sacar. Lo
///     detecta el agente (ver PuertoRele).
/// </summary>
public sealed class ConfigDelGimnasio
{
    private readonly ILogger<ConfigDelGimnasio> _log;
    private ConfigGym _actual;

    public ConfigDelGimnasio(AgentConfig cfg, ILogger<ConfigDelGimnasio> log)
    {
        _log = log;
        _actual = new ConfigGym(cfg.AutoDecide, cfg.TurnstileEnabled, cfg.RelayPulseMs, cfg.IdentifyThreshold);
    }

    /// <summary>Lo que vale AHORA. Se lee en el punto de uso, no se cachea en un campo.</summary>
    public ConfigGym Actual => Volatile.Read(ref _actual);

    /// <summary>"archivo" mientras el servidor no haya contestado nunca; después, "servidor".</summary>
    public string Origen { get; private set; } = "archivo";

    /// <summary>Cuándo contestó el servidor por última vez. null = todavía nunca.</summary>
    public DateTime? UltimaDelServidorUtc { get; private set; }

    /// <summary>
    /// Aplica lo que mandó el servidor. Sólo se llama cuando la RPC contestó de verdad: un
    /// fallo no llega hasta acá, para que un corte de red no vuelva a los defaults.
    /// </summary>
    public void AplicarDelServidor(ConfigGym nueva)
    {
        var previa = Volatile.Read(ref _actual);
        Volatile.Write(ref _actual, nueva);
        Origen = "servidor";
        UltimaDelServidorUtc = DateTime.UtcNow;

        // Sólo se anota lo que CAMBIÓ. Esto corre cada pocos minutos, para siempre: un log
        // por vuelta taparía el resto y nadie volvería a leer este archivo.
        if (previa == nueva) return;
        _log.LogInformation(
            "Config del gimnasio actualizada: abre sin navegador {A}->{B}, torniquete {C}->{D}, pulso {E}->{F} ms, umbral {G}->{H}",
            previa.AbreSinNavegador, nueva.AbreSinNavegador,
            previa.TieneTorniquete, nueva.TieneTorniquete,
            previa.PulsoMs, nueva.PulsoMs,
            previa.Umbral, nueva.Umbral);

        // El torniquete se resuelve al construir el relé, o sea al arrancar el agente: si
        // el gimnasio lo prende desde el panel, toma efecto en el próximo inicio de sesión.
        // No es un descuido — crear y destruir un puerto serie en caliente, en el camino
        // que abre la puerta, es más riesgo que esperar a mañana. Que quede DICHO en el log
        // es lo que evita que alguien lo persiga media hora.
        if (previa.TieneTorniquete != nueva.TieneTorniquete)
            _log.LogWarning("El torniquete cambió a {Estado}: toma efecto cuando se reinicie el agente (al iniciar sesión)",
                nueva.TieneTorniquete ? "ACTIVADO" : "desactivado");
    }
}

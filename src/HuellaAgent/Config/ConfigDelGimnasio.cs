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
    private readonly string? _archivoGuardado;
    private ConfigGym _actual;

    /// <param name="archivoGuardado">
    /// Dónde se guarda lo último que dijo el servidor (ProgramData\HuellaAgent\config-gym.json).
    /// null = no se guarda (los tests).
    ///
    /// POR QUÉ SE GUARDA. Hasta la v1.5.0 lo del servidor vivía sólo en memoria: en cada
    /// arranque se volvía a `appsettings.json` hasta que el servidor contestara. Y el relé
    /// se elige al arrancar. Resultado, visto el 05-oct en recepción: el instalador nuevo
    /// dejó un appsettings limpio (sin torniquete), el servidor decía "torniquete
    /// activado", y la puerta quedó con un relé SIMULADO — sin forma de salir, porque cada
    /// reinicio volvía a leer el mismo archivo. Guardarlo hace que la PC arranque con lo
    /// último que sabía del gimnasio, con o sin internet.
    /// </param>
    public ConfigDelGimnasio(AgentConfig cfg, ILogger<ConfigDelGimnasio> log, string? archivoGuardado = null)
    {
        _log = log;
        _archivoGuardado = archivoGuardado;
        _actual = new ConfigGym(cfg.AutoDecide, cfg.TurnstileEnabled, cfg.RelayPulseMs, cfg.IdentifyThreshold);

        var guardada = Leer(archivoGuardado);
        if (guardada is not null)
        {
            _actual = guardada;
            Origen = "servidor (guardada)";
            _log.LogInformation("Config del gimnasio: arranco con la ultima que mando el servidor (torniquete {T})",
                guardada.TieneTorniquete ? "activado" : "no");
        }
    }

    private static ConfigGym? Leer(string? archivo)
    {
        if (archivo is null || !File.Exists(archivo)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<ConfigGym>(File.ReadAllText(archivo)); }
        catch { return null; }   // archivo roto = como si no estuviera: manda appsettings
    }

    private void Guardar(ConfigGym c)
    {
        if (_archivoGuardado is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_archivoGuardado);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _archivoGuardado + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(c));
            File.Move(tmp, _archivoGuardado, overwrite: true);
        }
        catch (Exception ex)
        {
            // No poder guardarlo no frena nada: la config en memoria ya es la correcta.
            _log.LogWarning("No se pudo guardar la config del gimnasio: {Msg}", ex.Message);
        }
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

        // Se guarda la primera vez en este arranque aunque no haya cambiado (el archivo
        // puede no existir todavía) y después sólo cuando cambia: no se escribe a disco
        // cada pocos minutos lo mismo.
        if (previa != nueva || !_guardadaEnEsteArranque)
        {
            Guardar(nueva);
            _guardadaEnEsteArranque = true;
        }

        // Sólo se anota lo que CAMBIÓ. Esto corre cada pocos minutos, para siempre: un log
        // por vuelta taparía el resto y nadie volvería a leer este archivo.
        if (previa == nueva) return;
        _log.LogInformation(
            "Config del gimnasio actualizada: abre sin navegador {A}->{B}, torniquete {C}->{D}, pulso {E}->{F} ms, umbral {G}->{H}",
            previa.AbreSinNavegador, nueva.AbreSinNavegador,
            previa.TieneTorniquete, nueva.TieneTorniquete,
            previa.PulsoMs, nueva.PulsoMs,
            previa.Umbral, nueva.Umbral);

        // Desde la v1.5.1 toma efecto YA: el relé (ReleSegunGimnasio) mira esta config en
        // cada pulso. Antes decía "toma efecto al reiniciar", y era falso: al reiniciar se
        // volvía a leer el appsettings y el cambio no llegaba nunca.
        if (previa.TieneTorniquete != nueva.TieneTorniquete)
            _log.LogWarning("El torniquete cambió a {Estado}", nueva.TieneTorniquete ? "ACTIVADO" : "desactivado");
    }

    private bool _guardadaEnEsteArranque;
}

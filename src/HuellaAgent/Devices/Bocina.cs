using HuellaAgent.Config;

namespace HuellaAgent.Devices;

/// <summary>
/// El aviso sonoro para la persona que apoyó el dedo, por los parlantes de la PC.
///
/// Hace falta porque en la puerta no hay pantalla: el socio apoya el dedo y se queda
/// mirando el torniquete sin saber si lo leyó o si tiene que insistir. Cuando abre, el
/// torniquete mismo avisa; **el caso que necesita sonido es el que NO abre**.
///
/// Se hace por software y no por el lector: probado el 18-sep con el SLK20R, los
/// parámetros de luz y zumbador (101-103) aceptan valores, devuelven ok y no hacen nada.
///
/// `Console.Beep` va contra la API Beep de Windows, que desde Windows 7 sale por la placa
/// de sonido (no por el speaker viejo del gabinete). Suena en la PC de recepción, así que
/// sirve si está cerca de la puerta; si no, hay que poner un zumbador en el relé.
/// </summary>
public sealed class Bocina
{
    private readonly AgentConfig _cfg;
    private readonly ILogger<Bocina> _log;

    public Bocina(AgentConfig cfg, ILogger<Bocina> log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Pasa: un pitido corto y agudo.</summary>
    public void Ok() => Sonar(new[] { (1200, 120) });

    /// <summary>No pasa: dos pitidos graves, que no se confunden con el de arriba.</summary>
    public void Rechazo() => Sonar(new[] { (400, 180), (400, 180) });

    // Fire-and-forget: el sonido NO puede demorar la apertura de la puerta. Console.Beep
    // bloquea el hilo mientras dura, así que va aparte.
    private void Sonar((int freq, int ms)[] notas)
    {
        if (!_cfg.SonidoEnabled || !OperatingSystem.IsWindows()) return;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var (freq, ms) in notas)
                {
                    Console.Beep(freq, ms);
                    Thread.Sleep(60);   // silencio corto entre pitidos
                }
            }
            catch (Exception ex)
            {
                // PC sin salida de audio, o audio tomado por otra app: no es motivo para
                // que el portero deje de abrir la puerta.
                _log.LogDebug(ex, "No se pudo emitir el aviso sonoro");
            }
        });
    }
}

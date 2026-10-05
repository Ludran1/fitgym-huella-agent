using HuellaAgent.Config;

namespace HuellaAgent.Relays;

/// <summary>
/// El relé que usa el agente: el REAL si el gimnasio tiene torniquete, el simulado si no.
/// Y lo decide en cada uso, mirando la config vigente del gimnasio — no una vez al arrancar.
///
/// POR QUÉ. Hasta la v1.5.0 RelayFactory elegía al arrancar, leyendo sólo
/// `appsettings.json`. El servidor podía decir "este gym tiene torniquete" (lector_config)
/// y no cambiaba nada: el relé ya estaba elegido. El 05-oct, en recepción, el instalador
/// nuevo dejó un appsettings sin torniquete y la puerta quedó con un MockRelay que contestaba
/// "ok" a cada apertura — la puerta no abría y /health decía turnstile "ready".
///
/// El real se construye UNA vez, la primera vez que hace falta, y queda: no se crea y
/// destruye un puerto serie en caliente en el camino que abre la puerta. Si el gimnasio
/// apaga el torniquete, simplemente se deja de usar.
/// </summary>
public sealed class ReleSegunGimnasio : IRelay, IDisposable
{
    private readonly ConfigDelGimnasio _gym;
    private readonly Lazy<IRelay> _real;
    private readonly IRelay _simulado;

    public ReleSegunGimnasio(ConfigDelGimnasio gym, Func<IRelay> crearReal, IRelay simulado)
    {
        _gym = gym;
        _real = new Lazy<IRelay>(crearReal, LazyThreadSafetyMode.ExecutionAndPublication);
        _simulado = simulado;
    }

    /// <summary>Si el relé que se está usando es el de verdad. Para tests y diagnóstico.</summary>
    public bool UsaElReal => _gym.Actual.TieneTorniquete && OperatingSystem.IsWindows();

    private IRelay Vigente => UsaElReal ? _real.Value : _simulado;

    public bool IsConnected => Vigente.IsConnected;
    public string? LastError => Vigente.LastError;
    public bool TryConnect() => Vigente.TryConnect();
    public Task PulseAsync(int pulseMs, CancellationToken ct) => Vigente.PulseAsync(pulseMs, ct);

    public void Dispose()
    {
        if (_real.IsValueCreated && _real.Value is IDisposable d) d.Dispose();
    }
}

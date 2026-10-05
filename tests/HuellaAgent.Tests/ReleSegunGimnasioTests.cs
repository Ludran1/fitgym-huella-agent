using HuellaAgent.Config;
using HuellaAgent.Relays;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// El caso del 05-oct en recepción: el instalador nuevo dejó un appsettings SIN torniquete,
/// el servidor decía "torniquete activado", y la puerta quedó con un relé simulado que
/// contestaba ok a cada apertura. Ni reiniciando salía de ahí.
/// </summary>
public class ReleSegunGimnasioTests : IDisposable
{
    private readonly string _archivo = Path.Combine(Path.GetTempPath(), $"config-gym-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_archivo); } catch { } }

    private sealed class ReleDePrueba : IRelay
    {
        public int Pulsos;
        public bool IsConnected => true;
        public string? LastError => null;
        public bool TryConnect() => true;
        public Task PulseAsync(int pulseMs, CancellationToken ct) { Pulsos++; return Task.CompletedTask; }
    }

    private static ConfigGym ConTorniquete(bool si) => new(true, si, 700, 300);

    [Fact]
    public async Task Si_el_servidor_dice_torniquete_usa_el_REAL_aunque_el_archivo_diga_que_no()
    {
        var gym = new ConfigDelGimnasio(new AgentConfig { TurnstileEnabled = false }, NullLogger<ConfigDelGimnasio>.Instance);
        var real = new ReleDePrueba();
        var simulado = new ReleDePrueba();
        var rele = new ReleSegunGimnasio(gym, () => real, simulado);

        gym.AplicarDelServidor(ConTorniquete(true));
        await rele.PulseAsync(700, CancellationToken.None);

        Assert.Equal(1, real.Pulsos);
        Assert.Equal(0, simulado.Pulsos);
    }

    [Fact]
    public async Task Sin_torniquete_no_se_construye_el_real()
    {
        var gym = new ConfigDelGimnasio(new AgentConfig { TurnstileEnabled = false }, NullLogger<ConfigDelGimnasio>.Instance);
        var construido = false;
        var simulado = new ReleDePrueba();
        var rele = new ReleSegunGimnasio(gym, () => { construido = true; return new ReleDePrueba(); }, simulado);

        await rele.PulseAsync(700, CancellationToken.None);

        Assert.False(construido);   // no se abre un puerto serie en un gym sin torniquete
        Assert.Equal(1, simulado.Pulsos);
    }

    [Fact]
    public void Lo_que_mando_el_servidor_sobrevive_al_reinicio()
    {
        var cfg = new AgentConfig { TurnstileEnabled = false };
        var primero = new ConfigDelGimnasio(cfg, NullLogger<ConfigDelGimnasio>.Instance, _archivo);
        primero.AplicarDelServidor(ConTorniquete(true));

        // "Reinicio": un ConfigDelGimnasio nuevo, el mismo appsettings sin torniquete, y
        // el servidor todavía sin contestar (sin internet, o recién arrancado).
        var despues = new ConfigDelGimnasio(cfg, NullLogger<ConfigDelGimnasio>.Instance, _archivo);

        Assert.True(despues.Actual.TieneTorniquete);
        Assert.Equal(700, despues.Actual.PulsoMs);
        Assert.Equal("servidor (guardada)", despues.Origen);
    }

    [Fact]
    public void Un_archivo_guardado_roto_no_rompe_el_arranque()
    {
        File.WriteAllText(_archivo, "{ esto no es json");
        var gym = new ConfigDelGimnasio(new AgentConfig { TurnstileEnabled = true }, NullLogger<ConfigDelGimnasio>.Instance, _archivo);

        Assert.True(gym.Actual.TieneTorniquete);   // manda el appsettings, como antes
        Assert.Equal("archivo", gym.Origen);
    }
}

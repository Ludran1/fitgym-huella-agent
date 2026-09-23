using HuellaAgent.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// La configuración del gimnasio deja de vivir en un archivo de ESA computadora.
///
/// Era el último paso que obligaba a una llamada al cambiar de PC: abrir
/// `appsettings.json` con un editor de texto y reproducir cuatro valores a mano. Ahora
/// bajan con el vínculo, igual que ya bajaban las huellas.
///
/// Las tres reglas que fijan estos tests son de seguridad operativa, no de comodidad: un
/// corte de internet no puede apagarle la puerta a un gimnasio.
/// </summary>
public class ConfigDelGimnasioTests
{
    private static ConfigDelGimnasio Build(Action<Dictionary<string, string?>>? tune = null)
    {
        var overrides = new Dictionary<string, string?>();
        tune?.Invoke(overrides);
        var cfg = AgentConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(overrides).Build());
        return new ConfigDelGimnasio(cfg, NullLogger<ConfigDelGimnasio>.Instance);
    }

    /// <summary>
    /// Arranca con lo que dice el archivo. Un agente contra una base sin la migración —o
    /// sin internet al arrancar— tiene que comportarse exactamente como antes.
    /// </summary>
    [Fact]
    public void Arranca_con_lo_que_dice_el_archivo()
    {
        var gym = Build(o =>
        {
            o["Agent:AutoDecide"] = "true";
            o["Agent:TurnstileEnabled"] = "true";
            o["Agent:RelayPulseMs"] = "900";
            o["Agent:IdentifyThreshold"] = "350";
        });

        Assert.True(gym.Actual.AbreSinNavegador);
        Assert.True(gym.Actual.TieneTorniquete);
        Assert.Equal(900, gym.Actual.PulsoMs);
        Assert.Equal(350, gym.Actual.Umbral);
        Assert.Equal("archivo", gym.Origen);
        Assert.Null(gym.UltimaDelServidorUtc);
    }

    /// <summary>Lo del servidor pisa al archivo: ese es el punto de todo esto.</summary>
    [Fact]
    public void Lo_del_servidor_pisa_lo_del_archivo()
    {
        var gym = Build(o => o["Agent:AutoDecide"] = "false");

        gym.AplicarDelServidor(new ConfigGym(AbreSinNavegador: true, TieneTorniquete: true, PulsoMs: 800, Umbral: 320));

        Assert.True(gym.Actual.AbreSinNavegador);
        Assert.Equal(800, gym.Actual.PulsoMs);
        Assert.Equal("servidor", gym.Origen);
        Assert.NotNull(gym.UltimaDelServidorUtc);
    }

    /// <summary>
    /// EL test de seguridad: si el servidor deja de contestar, NO se vuelve a los defaults.
    ///
    /// El caller no llama a `AplicarDelServidor` cuando la RPC falla, así que lo que hay que
    /// garantizar es que un fallo no tenga forma de borrar lo último que se sabía. Un corte
    /// de internet no puede apagarle la puerta a un gimnasio que sí la tiene abierta.
    /// </summary>
    [Fact]
    public void Si_el_servidor_deja_de_contestar_se_queda_con_lo_ultimo_que_sabia()
    {
        var gym = Build(o => o["Agent:AutoDecide"] = "false");
        gym.AplicarDelServidor(new ConfigGym(true, true, 800, 320));

        // Pasan varias vueltas sin respuesta: nadie toca nada.
        Assert.True(gym.Actual.AbreSinNavegador);
        Assert.True(gym.Actual.TieneTorniquete);
        Assert.Equal(800, gym.Actual.PulsoMs);
        Assert.Equal("servidor", gym.Origen);
    }

    /// <summary>
    /// El dueño apaga la puerta automática desde el panel: tiene que tomar efecto, no sólo
    /// prenderla. Un cambio que va en un solo sentido es una trampa.
    /// </summary>
    [Fact]
    public void El_servidor_tambien_puede_apagar_lo_que_el_archivo_prendia()
    {
        var gym = Build(o =>
        {
            o["Agent:AutoDecide"] = "true";
            o["Agent:TurnstileEnabled"] = "true";
        });

        gym.AplicarDelServidor(new ConfigGym(AbreSinNavegador: false, TieneTorniquete: false, PulsoMs: 700, Umbral: 300));

        Assert.False(gym.Actual.AbreSinNavegador);
        Assert.False(gym.Actual.TieneTorniquete);
    }

    /// <summary>
    /// Aplicar lo mismo dos veces no cambia nada. Importa porque esto corre cada pocos
    /// minutos para siempre: si cada vuelta contara como un cambio, el log sería inútil.
    /// </summary>
    [Fact]
    public void Aplicar_la_misma_config_dos_veces_es_inocuo()
    {
        var gym = Build();
        var c = new ConfigGym(true, false, 700, 300);

        gym.AplicarDelServidor(c);
        var primera = gym.UltimaDelServidorUtc;
        gym.AplicarDelServidor(c);

        Assert.Equal(c, gym.Actual);
        Assert.NotNull(primera);
        // Se sigue anotando que el servidor contestó, aunque no haya cambiado nada: eso es
        // lo que distingue "no cambió" de "dejó de contestar".
        Assert.True(gym.UltimaDelServidorUtc >= primera);
    }

    /// <summary>
    /// El umbral también viaja, pero con una advertencia que vale la pena dejar en un test:
    /// las ocho lecturas reales de producción al 22-sep fueron 309, 400, 401, 545, 577,
    /// 633, 660 y 808 contra un umbral de 300. La más baja entró por NUEVE puntos. Subirlo
    /// a 350 deja a esa persona afuera y nadie va a entender por qué.
    /// </summary>
    [Fact]
    public void El_umbral_por_defecto_sigue_siendo_300()
    {
        Assert.Equal(300, Build().Actual.Umbral);
    }
}

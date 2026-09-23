using HuellaAgent.Config;
using HuellaAgent.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Json;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// El dedo de un enrolado no es alguien entrando al gimnasio.
///
/// EL BUG. Reportado desde el mostrador el 22-sep. `tomarSensor()` del frontend apaga el
/// listener del NAVEGADOR, pero `PorteroService` es un BackgroundService del agente y no se
/// entera de nada: con el modal de enrolar abierto sigue consumiendo dedos. Al socio que
/// esta siendo dado de alta —si ya tenia huella— le marca asistencia y le abre la puerta en
/// medio de su propia alta.
///
/// Suspender el lector durante cada `/capture` no alcanzaba: un enrolado son TRES capturas
/// con huecos en el medio, y en esos huecos el dedo sigue sobre el vidrio. El caso concreto
/// es el frontend descartando un apoyo repetido y esperando 400 ms antes de volver a pedir.
/// </summary>
public class EnroladoAisladoTests
{
    private static (FingerprintScanner, FakeDevice) Build(int graciaMs = 2500)
    {
        var cfg = AgentConfig.Load(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Agent:ContinuousScan"] = "true",
                ["Agent:ScanPollMs"] = "10",
                ["Agent:SameFingerDebounceMs"] = "20",
                ["Agent:EnroladoGraciaMs"] = graciaMs.ToString(),
            }).Build());
        var device = new FakeDevice { IdentifyNoFinger = true };   // arranca "sin dedo"
        return (new FingerprintScanner(device, cfg, NullLogger<FingerprintScanner>.Instance), device);
    }

    /// <summary>
    /// EL test. El dedo esta apoyado, hay un enrolado en curso, y el portero —que es quien
    /// llama a WaitForProbeAsync— se queda con las manos vacias.
    /// </summary>
    [Fact]
    public async Task Con_un_enrolado_en_curso_el_dedo_no_le_llega_al_portero()
    {
        var (scanner, device) = Build();
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            using var enrolando = await scanner.SuspendAsync(CancellationToken.None);
            device.IdentifyNoFinger = false;      // el socio apoya el dedo para enrolarse

            var probe = await scanner.WaitForProbeAsync(1, CancellationToken.None);

            Assert.Null(probe);   // si llegara, seria una asistencia que nadie marco
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// El hueco ENTRE capturas, que es por donde se colaba. El lector se suelta al terminar
    /// una captura y se vuelve a tomar para la siguiente; en el medio el dedo sigue apoyado
    /// y el loop, libre, lo lee y lo publica.
    /// </summary>
    [Fact]
    public async Task Entre_una_captura_y_la_siguiente_el_dedo_sigue_siendo_del_enrolado()
    {
        var (scanner, device) = Build();
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            // Primera captura del enrolado: se toma y se suelta el lector.
            (await scanner.SuspendAsync(CancellationToken.None)).Dispose();

            // El socio NO levanta el dedo. El loop lo lee y trata de publicarlo.
            device.IdentifyNoFinger = false;
            await Task.Delay(150);

            var probe = await scanner.WaitForProbeAsync(1, CancellationToken.None);

            Assert.Null(probe);
            Assert.True(scanner.EnrolandoAhora, "la gracia deberia seguir corriendo");
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// Y la otra mitad, que importa igual: la gracia CADUCA sola. Si no, un enrolado dejaria
    /// la puerta muerta para siempre y nadie sabria por que — el agente no tiene forma de
    /// que le avisen que el modal se cerro.
    /// </summary>
    [Fact]
    public async Task Cuando_termina_el_enrolado_el_lector_vuelve_a_leer_solo()
    {
        var (scanner, device) = Build(graciaMs: 150);
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            (await scanner.SuspendAsync(CancellationToken.None)).Dispose();
            Assert.True(scanner.EnrolandoAhora);

            await Task.Delay(300);                // se cerro el modal, paso la gracia
            Assert.False(scanner.EnrolandoAhora);

            device.IdentifyNoFinger = false;      // ahora si, alguien entra al gimnasio
            var probe = await scanner.WaitForProbeAsync(2, CancellationToken.None);

            Assert.NotNull(probe);
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// La carrera fina, la que motivo HU-10: `Publish()` corre FUERA del `_deviceLock` —el
    /// loop captura con el lock tomado y publica despues de soltarlo—, asi que un dedo leido
    /// justo antes de que entrara el enrolado podia caer en el buffer DESPUES de que
    /// `SuspendAsync` lo habia limpiado.
    ///
    /// Un test de carrera que pasa no prueba que la carrera no exista, asi que esto no la
    /// dispara: verifica la INVARIANTE que la hace imposible — con el enrolado marcado,
    /// ningun Publish entra, venga de donde venga.
    /// </summary>
    [Fact]
    public async Task Un_publish_tardio_no_entra_al_buffer_si_ya_empezo_el_enrolado()
    {
        var (scanner, device) = Build();
        device.IdentifyNoFinger = false;          // hay dedo desde el arranque
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);                // el loop viene publicando
            using var enrolando = await scanner.SuspendAsync(CancellationToken.None);

            // Cualquier publicacion que llegue de acá en más —incluida una que venia en
            // camino— tiene que rebotar. Y el buffer quedo limpio al suspender.
            await Task.Delay(100);
            Assert.Null(await scanner.WaitForProbeAsync(1, CancellationToken.None));
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// HU-9 · El agente SABE si el dedo sigue apoyado — `TryCapture()` devuelve null cuando
    /// el vidrio esta libre. El navegador no: solo puede adivinarlo por tiempos (descarta una
    /// captura que vuelve en menos de 500 ms).
    ///
    /// Con el dedo que nunca se levanta, la captura sale igual —trabar el enrolado con un
    /// error que el mostrador no puede accionar seria peor— pero AVISADA, y el frontend la
    /// descarta con certeza en vez de con un cronometro.
    /// </summary>
    [Fact]
    public async Task Una_captura_del_mismo_apoyo_sale_avisada()
    {
        await using var app = new AgentFactory();
        var http = app.CreateClient();

        // Primera captura: no exige levantar nada, con que apoyo se arranca da igual.
        var uno = await http.PostAsJsonAsync("/api/fingerprint/capture", new { timeout = 1 });
        var cuerpoUno = await uno.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(cuerpoUno.GetProperty("mismo_apoyo").GetBoolean());

        // Segunda, con el dedo TODAVIA sobre el vidrio (TryCapture nunca devuelve null).
        var dos = await http.PostAsJsonAsync("/api/fingerprint/capture", new { timeout = 1 });
        var cuerpoDos = await dos.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Equal(System.Net.HttpStatusCode.OK, dos.StatusCode);
        Assert.True(cuerpoDos.GetProperty("mismo_apoyo").GetBoolean(),
            "las tres capturas saldrian del mismo apoyo y la huella naceria angosta");
    }

    /// <summary>
    /// Y si el socio SI levanta el dedo, la captura sale limpia. Es la otra mitad: un aviso
    /// que salta siempre no sirve de nada.
    /// </summary>
    [Fact]
    public async Task Si_levanta_el_dedo_la_captura_sale_limpia()
    {
        await using var app = new AgentFactory();
        var http = app.CreateClient();

        await http.PostAsJsonAsync("/api/fingerprint/capture", new { timeout = 1 });

        // Levanto el dedo: TryCapture pasa a devolver null, pero CaptureAsync sigue dando
        // el template (es el dedo que apoya de nuevo, un instante despues).
        app.Device.IdentifyNoFinger = true;

        var dos = await http.PostAsJsonAsync("/api/fingerprint/capture", new { timeout = 1 });
        var cuerpo = await dos.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.False(cuerpo.GetProperty("mismo_apoyo").GetBoolean());
    }

    /// <summary>
    /// Si el pedido de captura se cancela esperando el lector (el mostrador cerro el modal
    /// antes de que le tocara el turno), la marca tiene que soltarse igual. Si no, un
    /// enrolado que nunca empezo dejaria al portero mudo.
    /// </summary>
    [Fact]
    public async Task Un_enrolado_cancelado_antes_de_empezar_no_deja_la_puerta_muerta()
    {
        var (scanner, _) = Build(graciaMs: 50);
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            using var ocupado = await scanner.SuspendAsync(CancellationToken.None);   // otro lo tiene

            using var cts = new CancellationTokenSource(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => scanner.SuspendAsync(cts.Token));

            await Task.Delay(150);
            // El que SI tiene el lector sigue marcando enrolado; lo que no puede pasar es
            // que el cancelado haya dejado la marca en infinito.
            ocupado.Dispose();
            await Task.Delay(150);
            Assert.False(scanner.EnrolandoAhora);
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }
}

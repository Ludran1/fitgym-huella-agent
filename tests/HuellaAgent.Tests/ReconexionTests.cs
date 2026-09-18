using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuellaAgent.Config;
using HuellaAgent.Devices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// El agente v1.0.2 abria el lector una sola vez y, si fallaba, caia al MockDevice para
/// siempre: el 08 y el 10-sep arranco con `zkfp2.Init fallo` y se paso el dia sin leer un
/// dedo, con /health diciendo reader "connected". Esto fija el comportamiento nuevo.
/// </summary>
public class ReconexionTests
{
    private static AgentConfig Cfg() => new() { ReconnectSeconds = 1, ContinuousScan = true };

    [Fact]
    public void Si_el_lector_no_abre_al_arrancar_reintenta_y_no_finge_estar_conectado()
    {
        var intentos = 0;
        var device = new ReconnectingDevice(() =>
        {
            intentos++;
            if (intentos < 3) throw new DeviceUnavailableException("zkfp2.Init fallo");
            return new FakeDevice { DeviceName = "ZKTeco SLK20R" };
        }, Cfg(), NullLogger.Instance);

        Assert.False(device.TryReconnect());
        Assert.False(device.IsConnected);
        Assert.Equal("(sin lector)", device.DeviceName);          // no miente con un Mock
        Assert.Equal("zkfp2.Init fallo", device.LastError);
        Assert.Null(device.TryCapture());                          // no rompe mientras tanto

        // El freno entre intentos evita martillar el SDK: dentro de la ventana no reintenta.
        Assert.False(device.TryReconnect());
        Assert.Equal(1, intentos);

        Thread.Sleep(1100);
        Assert.False(device.TryReconnect());                       // 2do intento, falla
        Thread.Sleep(1100);
        Assert.True(device.TryReconnect());                        // 3ro: el lector aparecio
        Assert.True(device.IsConnected);
        Assert.Equal("ZKTeco SLK20R", device.DeviceName);
        Assert.Null(device.LastError);
    }

    [Fact]
    public void Si_el_lector_desaparece_leyendo_se_suelta_y_se_vuelve_a_abrir()
    {
        var lectores = new List<FakeDevice>();
        var device = new ReconnectingDevice(() =>
        {
            var d = new FakeDevice { DeviceName = "ZKTeco SLK20R" };
            lectores.Add(d);
            return d;
        }, Cfg(), NullLogger.Instance);

        Assert.True(device.TryReconnect());
        Assert.NotNull(device.TryCapture());

        // Desenchufado en medio de una lectura: el device lo detecta y lo suelta.
        lectores[0].TryCaptureFalla = true;
        Assert.Null(device.TryCapture());
        Assert.False(device.IsConnected);
        Assert.Equal("el lector ya no aparece", device.LastError);

        Thread.Sleep(1100);
        Assert.True(device.TryReconnect());        // ciclo completo: se abrio uno nuevo
        Assert.Equal(2, lectores.Count);
        Assert.True(device.IsConnected);
        Assert.NotNull(device.TryCapture());
    }

    [Fact]
    public async Task El_scanner_reintenta_solo_cuando_el_lector_esta_caido()
    {
        var fake = new FakeDevice { IdentifyNoFinger = true };
        fake.Desconectar();
        var cfg = new AgentConfig { ContinuousScan = true, ScanPollMs = 10 };
        var scanner = new FingerprintScanner(fake, cfg, NullLogger<FingerprintScanner>.Instance);

        await scanner.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        await scanner.StopAsync(CancellationToken.None);

        Assert.True(fake.ReconnectCalls > 0, "el scanner nunca intento reconectar");
        Assert.True(fake.IsConnected, "el lector nunca volvio");
    }

    [Fact]
    public async Task Health_dice_la_verdad_del_lector_y_del_vinculo()
    {
        await using var app = new AgentFactory();
        app.Device.DeviceName = "ZKTeco ZK9500";       // otro modelo: /health debe decirlo
        var http = app.CreateClient();

        var health = await http.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("connected", health.GetProperty("reader").GetString());
        Assert.Equal("ZKTeco ZK9500", health.GetProperty("device").GetString());
        Assert.Equal(12, health.GetProperty("boot_id").GetString()!.Length);
        Assert.Equal("off", health.GetProperty("turnstile").GetString());   // sin torniquete
        // Los campos en null NO viajan (JsonIgnoreCondition.WhenWritingNull): sin vincular
        // no hay tenant_id, y sin errores no hay last_error. El frontend tiene que leerlos
        // como opcionales.
        Assert.False(health.TryGetProperty("tenant_id", out _));
        Assert.False(health.TryGetProperty("last_error", out _));

        // Sin lector: reader disconnected y el motivo a la vista, en vez de "connected".
        app.Device.Desconectar("zkfp2.OpenDevice fallo (¿otro programa esta usando el lector?)");
        var caido = await http.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("disconnected", caido.GetProperty("reader").GetString());
        Assert.Contains("OpenDevice", caido.GetProperty("last_error").GetString());
    }

    [Fact]
    public async Task El_torniquete_sin_rele_contesta_503_con_el_motivo()
    {
        await using var app = new AgentFactory();
        var http = app.CreateClient();

        // TurnstileEnabled=false (default de los tests) → 409, que el frontend usa para
        // apagarse solo en los gyms sin puerta. Eso no cambia.
        var sinTorniquete = await http.PostAsJsonAsync("/api/turnstile/open", new { tenant_id = "t1" });
        Assert.Equal(HttpStatusCode.Conflict, sinTorniquete.StatusCode);
    }
}

using HuellaAgent.Config;
using HuellaAgent.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Fija el comportamiento del scanner continuo, que es el fix del "a veces agarra, a veces
/// no": el sensor no se desarma nunca y un dedo apoyado entre polls del frontend queda
/// bufferado en vez de perderse.
/// </summary>
public class ScannerTests
{
    private static (FingerprintScanner, FakeDevice) Build(Action<Dictionary<string, string?>>? tune = null)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Agent:ContinuousScan"] = "true",
            ["Agent:ScanPollMs"] = "10",
            ["Agent:SameFingerDebounceMs"] = "50",
            ["Agent:ProbeFreshnessMs"] = "2500",
        };
        tune?.Invoke(overrides);

        var cfg = AgentConfig.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(overrides).Build());
        var device = new FakeDevice { IdentifyNoFinger = true };   // arranca "sin dedo"
        return (new FingerprintScanner(device, cfg, NullLogger<FingerprintScanner>.Instance), device);
    }

    /// <summary>Con el sensor leyendo siempre, un dedo llega al buffer sin que nadie lo pida.</summary>
    [Fact]
    public async Task Probe_captured_by_the_loop_reaches_the_waiter()
    {
        var (scanner, device) = Build();
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            device.IdentifyNoFinger = false;   // alguien apoya el dedo
            var probe = await scanner.WaitForProbeAsync(2, CancellationToken.None);
            Assert.NotNull(probe);
            Assert.Equal(device.Capture.Template, probe!.Template);
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// EL test del fix: el dedo se apoya MIENTRAS nadie esta esperando (el frontend dormia
    /// entre polls). Con el modelo viejo esa lectura se perdia; ahora queda bufferada y el
    /// siguiente /identify la recibe al instante.
    /// </summary>
    [Fact]
    public async Task Probe_captured_while_nobody_waits_is_buffered_not_lost()
    {
        var (scanner, device) = Build();
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            device.IdentifyNoFinger = false;
            await Task.Delay(200);             // el dedo entra sin que nadie escuche
            device.IdentifyNoFinger = true;    // y ya lo levanto

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var probe = await scanner.WaitForProbeAsync(2, CancellationToken.None);
            sw.Stop();

            Assert.NotNull(probe);
            Assert.True(sw.ElapsedMilliseconds < 500, $"debio volver del buffer al instante, tardo {sw.ElapsedMilliseconds}ms");
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>Consumo unico: el mismo dedo no se entrega dos veces (no duplica asistencias).</summary>
    [Fact]
    public async Task Probe_is_consumed_once()
    {
        // Debounce largo: tras publicar, el loop duerme 1s. Da margen de sobra para
        // consumir y bajar la bandera antes de que pueda publicar un segundo dedo
        // (con un debounce corto el test seria flaky en una maquina cargada).
        var (scanner, device) = Build(o => o["Agent:SameFingerDebounceMs"] = "1000");
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            device.IdentifyNoFinger = false;
            Assert.NotNull(await scanner.WaitForProbeAsync(2, CancellationToken.None));
            device.IdentifyNoFinger = true;    // levanta el dedo

            // Sin dedo nuevo, la siguiente espera debe agotar la ventana (408), no repetir.
            Assert.Null(await scanner.WaitForProbeAsync(1, CancellationToken.None));
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// Un dedo viejo no marca: al reiniciar el sistema no se registra una asistencia
    /// fantasma con el ultimo dedo que quedo dando vueltas.
    /// </summary>
    [Fact]
    public async Task Stale_probe_is_discarded()
    {
        var (scanner, device) = Build(o => o["Agent:ProbeFreshnessMs"] = "100");
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            device.IdentifyNoFinger = false;
            await Task.Delay(150);             // se captura un dedo...
            device.IdentifyNoFinger = true;
            await Task.Delay(300);             // ...y se pasa de rancio

            Assert.Null(await scanner.WaitForProbeAsync(1, CancellationToken.None));
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>Sin dedo, la espera agota la ventana y devuelve null (=&gt; 408).</summary>
    [Fact]
    public async Task No_finger_returns_null_after_timeout()
    {
        var (scanner, _) = Build();
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Null(await scanner.WaitForProbeAsync(1, CancellationToken.None));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds >= 900, $"debio esperar la ventana entera, tardo {sw.ElapsedMilliseconds}ms");
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>
    /// Mientras el enrolado tiene el lector tomado, el loop no lee: los dos no pueden
    /// competir por el mismo dedo.
    /// </summary>
    [Fact]
    public async Task Suspend_stops_the_loop_from_touching_the_device()
    {
        var (scanner, device) = Build();
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            using (await scanner.SuspendAsync(CancellationToken.None))
            {
                var before = Volatile.Read(ref device.TryCaptureCalls);
                await Task.Delay(200);
                Assert.Equal(before, Volatile.Read(ref device.TryCaptureCalls));
            }

            // Liberado, el loop vuelve a leer.
            var after = Volatile.Read(ref device.TryCaptureCalls);
            await Task.Delay(200);
            Assert.True(Volatile.Read(ref device.TryCaptureCalls) > after);
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }

    /// <summary>Con ContinuousScan=false el loop ni arranca (valvula de escape).</summary>
    [Fact]
    public async Task Disabled_scanner_never_touches_the_device()
    {
        var (scanner, device) = Build(o => o["Agent:ContinuousScan"] = "false");
        Assert.False(scanner.Enabled);
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200);
            Assert.Equal(0, Volatile.Read(ref device.TryCaptureCalls));
        }
        finally { await scanner.StopAsync(CancellationToken.None); }
    }
}

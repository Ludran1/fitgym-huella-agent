using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HuellaAgent.Devices;

namespace HuellaAgent.Tests;

/// <summary>
/// WebApplicationFactory con storage en un archivo temporal unico (aislamiento por test)
/// y el FakeDevice inyectado. Permite testear el contrato HTTP completo sin SDK ni hardware.
///
/// Por defecto el scanner continuo va APAGADO: su loop corre en tiempo real y publicaria
/// dedos de fondo, volviendo flaky a los tests de contrato (un test que setea
/// IdentifyNoFinger DESPUES de arrancar competiria contra un dedo ya bufferado). Los tests
/// del scanner lo prenden explicitamente (continuousScan: true) o lo prueban directo,
/// sin HTTP, en ScannerTests.
/// </summary>
public sealed class AgentFactory : WebApplicationFactory<Program>
{
    public FakeDevice Device { get; } = new();
    private readonly string _storagePath;
    private readonly string? _apiKey;
    private readonly bool _continuousScan;

    public AgentFactory(string? apiKey = null, bool continuousScan = false)
    {
        _apiKey = apiKey;
        _continuousScan = continuousScan;
        _storagePath = Path.Combine(Path.GetTempPath(), $"huella-test-{Guid.NewGuid():N}.json");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var overrides = new Dictionary<string, string?>
            {
                ["Agent:StoragePath"] = _storagePath,
                ["Agent:IdentifyTimeoutSeconds"] = "1",
                ["Agent:ContinuousScan"] = _continuousScan ? "true" : "false",
            };
            if (_apiKey is not null) overrides["Agent:ApiKey"] = _apiKey;
            cfg.AddInMemoryCollection(overrides);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFingerprintDevice>();
            services.AddSingleton<IFingerprintDevice>(Device);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_storagePath)) File.Delete(_storagePath);
    }
}

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
/// </summary>
public sealed class AgentFactory : WebApplicationFactory<Program>
{
    public FakeDevice Device { get; } = new();
    private readonly string _storagePath;
    private readonly string? _apiKey;

    public AgentFactory(string? apiKey = null)
    {
        _apiKey = apiKey;
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

using System.Text.Json;
using System.Text.Json.Serialization;
using HuellaAgent.Api;
using HuellaAgent.Config;
using HuellaAgent.Devices;
using HuellaAgent.Relays;
using HuellaAgent.Storage;
using HuellaAgent.Supabase;

var builder = WebApplication.CreateBuilder(args);

// AgentConfig se resuelve por DI para ver la config FINAL (incluye overrides de tests via
// WebApplicationFactory, que se mergean recien al build). bootCfg es solo para el puerto.
var bootCfg = AgentConfig.Load(builder.Configuration);
builder.Services.AddSingleton(sp => AgentConfig.Load(sp.GetRequiredService<IConfiguration>()));

// Correr como Windows Service en produccion (no-op fuera de Windows).
if (OperatingSystem.IsWindows())
    builder.Host.UseWindowsService();

// Kestrel SOLO en localhost: el agente nunca se expone a la red.
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(bootCfg.Port));

// JSON snake_case para calzar con el contrato de huellaApi.ts.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// CORS: una pagina admin https puede llamar a localhost (Secure Context).
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<ITemplateStore>(sp =>
    new LocalFileStore(sp.GetRequiredService<AgentConfig>().StoragePath));
builder.Services.AddSingleton<IFingerprintDevice>(sp =>
    DeviceFactory.Create(sp.GetRequiredService<AgentConfig>(), sp.GetRequiredService<ILogger<Program>>()));
builder.Services.AddSingleton<IRelay>(sp =>
    RelayFactory.Create(sp.GetRequiredService<AgentConfig>(), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<HuellaRpc>();

var app = builder.Build();
app.UseCors();
FingerprintEndpoints.Map(app);

// Carga inicial: si la persistencia durable esta activa, baja los templates del tenant.
// Reader-first (Supabase OFF) salta esto y usa solo el archivo local.
var runCfg = app.Services.GetRequiredService<AgentConfig>();
var rpc = app.Services.GetRequiredService<HuellaRpc>();
var store = app.Services.GetRequiredService<ITemplateStore>();
var startLog = app.Services.GetRequiredService<ILogger<Program>>();
if (rpc.Enabled)
{
    try
    {
        var result = await rpc.TemplatesAsync(CancellationToken.None);
        if (result is not null)
        {
            // Keyear el cache local por el tenant_id REAL (el que manda el frontend en
            // enroll/identify), no por el token → si no, el identify no encontraría nada.
            foreach (var r in result.Templates)
                await store.SaveAsync(result.TenantId, r.ClienteId, r.Template);
            startLog.LogInformation("Carga inicial: {Count} templates desde Supabase (tenant {Tid})",
                result.Templates.Count, result.TenantId);
        }
    }
    catch (Exception ex) { startLog.LogWarning(ex, "No se pudo cargar templates al inicio (sigue offline)"); }
}

startLog.LogInformation("HuellaAgent escuchando en http://localhost:{Port} (device: {Device})",
    runCfg.Port, app.Services.GetRequiredService<IFingerprintDevice>().DeviceName);

app.Run();

// Para WebApplicationFactory en los tests.
public partial class Program { }

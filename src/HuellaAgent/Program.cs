using System.Text.Json;
using System.Text.Json.Serialization;
using HuellaAgent.Api;
using HuellaAgent.Config;
using HuellaAgent.Devices;
using HuellaAgent.Relays;
using HuellaAgent.Storage;
using HuellaAgent.Supabase;
using Serilog;

// El appsettings.json se busca al lado del EXE, no en el directorio actual.
//
// `CreateBuilder(args)` a secas usa Directory.GetCurrentDirectory() como content root, y de
// ahi lee appsettings.json. En recepcion funcionaba de pura casualidad: run-hidden.vbs hace
// `sh.CurrentDirectory = dir` antes de llamar a launch.ps1. Lanzado desde cualquier otra
// carpeta —un launch.ps1 a mano, la tarea sin "Iniciar en", el exe por doble clic— el
// archivo no aparecia y TODA la config del gimnasio volvia a los defaults en silencio:
// UseRealDevice=false (lector simulado reportando "connected"), sin torniquete, sin pulso.
// Paso el 26-sep en la maquina de desarrollo. El content root ahora es la carpeta del exe.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// AgentConfig se resuelve por DI para ver la config FINAL (incluye overrides de tests via
// WebApplicationFactory, que se mergean recien al build). bootCfg es solo para el puerto.
var bootCfg = AgentConfig.Load(builder.Configuration);
builder.Services.AddSingleton(sp => AgentConfig.Load(sp.GetRequiredService<IConfiguration>()));

// ── Logging a ARCHIVO persistente + consola ──────────────────────────────────
// El agente corre OCULTO (tarea al logon) → su consola no se ve. Sin esto, los errores
// se pierden. Serilog escribe a logs\agent-YYYYMMDD.log (rota diario, guarda 14 dias) en
// la misma carpeta del store (%ProgramData%\HuellaAgent\logs). Se puede leer en vivo por
// GET /logs. `shared:true` permite que /logs lea mientras el agente escribe.
var logsDir = Path.Combine(Path.GetDirectoryName(bootCfg.StoragePath) ?? ".", "logs");
try { Directory.CreateDirectory(logsDir); } catch { /* si falla, igual loguea a consola */ }
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logsDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

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

// CORS: una pagina admin https puede llamar a localhost (Secure Context), pero SOLO el
// panel. Con `*` —como estaba— y la api-key vacia, cualquier pagina abierta en el
// navegador de la PC de recepcion podia pedirle capturas al lector.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(bootCfg.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<ITemplateStore>(sp =>
    new LocalFileStore(sp.GetRequiredService<AgentConfig>().StoragePath));
builder.Services.AddSingleton(sp => new PairingStore(
    Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<AgentConfig>().StoragePath) ?? ".", "pairing.dat")));
builder.Services.AddSingleton<IFingerprintDevice>(sp =>
    DeviceFactory.Create(sp.GetRequiredService<AgentConfig>(), sp.GetRequiredService<ILogger<Program>>()));
// Scanner continuo: mantiene el sensor leyendo siempre y bufferea el ultimo dedo, para
// que /identify no tenga que armar el lector por request (esa era la zona muerta del
// ~25% que hacia que "a veces agarre y a veces no"). Singleton + hosted service: la
// MISMA instancia que inyectan los endpoints es la que corre el loop.
builder.Services.AddSingleton<FingerprintScanner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FingerprintScanner>());
// El portero (Agent:AutoDecide): el agente decide y abre sin navegador. Se apaga solo si
// el flag está en false, que es el default hasta que cada gym lo estrene. La bocina es su
// aviso sonoro: en la puerta no hay pantalla y el lector no tiene luz ni zumbador propios.
builder.Services.AddSingleton<Bocina>();
builder.Services.AddHostedService<PorteroService>();
builder.Services.AddSingleton<IRelay>(sp =>
    RelayFactory.Create(sp.GetRequiredService<AgentConfig>(), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<HuellaRpc>();

// La configuracion VIGENTE del gimnasio. Arranca desde appsettings.json y la pisa el
// servidor cuando contesta (huella_config). Ver ConfigDelGimnasio.
builder.Services.AddSingleton<ConfigDelGimnasio>();

// El latido: le cuenta al servidor como esta este lector y se baja la config del gimnasio.
// Va SEPARADO del portero a proposito — no puede tocar el lector ni demorar una apertura.
builder.Services.AddHostedService<LatidoService>();

try
{
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
                //
                // REEMPLAZA, no fusiona: la lista del servidor es la verdad completa. Antes
                // era un SaveAsync por template, que agrega y pisa pero nunca saca — un socio
                // borrado del panel seguía abriendo la puerta en esta PC para siempre.
                await store.ReemplazarAsync(result.TenantId, result.Templates);
                startLog.LogInformation("Carga inicial: {Count} templates desde Supabase (tenant {Tid})",
                    result.Templates.Count, result.TenantId);
            }
        }
        catch (Exception ex) { startLog.LogWarning(ex, "No se pudo cargar templates al inicio (sigue offline)"); }
    }

    startLog.LogInformation("HuellaAgent v{Version} escuchando en http://localhost:{Port} (device: {Device})",
        typeof(Program).Assembly.GetName().Version?.ToString(3), runCfg.Port,
        app.Services.GetRequiredService<IFingerprintDevice>().DeviceName);

    app.Run();
}
catch (Exception ex)
{
    // Crash fatal al iniciar (puerto ocupado, SDK roto, etc.) → queda en el log en vez de
    // desaparecer en silencio (el agente corre oculto).
    Log.Fatal(ex, "HuellaAgent fallo fatal al iniciar");
}
finally
{
    Log.CloseAndFlush();
}

// Para WebApplicationFactory en los tests.
public partial class Program { }

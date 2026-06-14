namespace HuellaAgent.Config;

/// <summary>
/// Configuracion del agente. Se lee de appsettings.json + variables de entorno.
/// El token del tenant NO se guarda aca en claro en produccion: el flujo de pairing
/// lo cifra con DPAPI (LocalMachine). Para la prueba reader-first se puede setear directo.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>Puerto local de Kestrel. El frontend espera 8000 (huellaApi.ts).</summary>
    public int Port { get; init; } = 8000;

    /// <summary>x-api-key opcional. Vacio = sin auth (dev). Si se setea, /api/* lo exige.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Ventana de captura del identify, en segundos. Decidido 2026-06-14 = 3-5s.</summary>
    public int IdentifyTimeoutSeconds { get; init; } = 4;

    /// <summary>Umbral de DBIdentify del SDK. R1: calibrar con hardware real (FAR/FRR).</summary>
    public int IdentifyThreshold { get; init; } = 0;

    /// <summary>Ruta del archivo de templates local (store offline-resiliente).</summary>
    public string StoragePath { get; init; } = DefaultStoragePath();

    /// <summary>Forzar el SDK real (solo aplica en Windows). En Linux/CI siempre Mock.</summary>
    public bool UseRealDevice { get; init; } = false;

    // ── Supabase (persistencia durable opcional; OFF para reader-first) ──────────
    public bool SupabaseEnabled { get; init; } = false;
    public string SupabaseUrl { get; init; } = "";
    public string SupabaseAnonKey { get; init; } = "";
    /// <summary>kiosk_token del tenant (uuid). En prod lo entrega el pairing.</summary>
    public string KioskToken { get; init; } = "";

    // ── Torniquete (Fase 7, opcional por gym) ───────────────────────────────────
    public bool TurnstileEnabled { get; init; } = false;
    /// <summary>Puerto serie del modulo USB-rele (ej. COM3 / /dev/ttyUSB0).</summary>
    public string RelayPort { get; init; } = "";
    /// <summary>Duracion del pulso de contacto seco, en ms (0.5-1s).</summary>
    public int RelayPulseMs { get; init; } = 700;

    public static AgentConfig Load(IConfiguration cfg)
    {
        var a = new AgentConfig();
        cfg.GetSection("Agent").Bind(a);
        return a;
    }

    private static string DefaultStoragePath()
    {
        // En Windows: %ProgramData%\HuellaAgent\templates.json (sobrevive al usuario).
        // En Linux/dev: ./data/templates.json relativo al cwd.
        if (OperatingSystem.IsWindows())
        {
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(pd, "HuellaAgent", "templates.json");
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "data", "templates.json");
    }
}

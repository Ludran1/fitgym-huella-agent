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

    // -- Scanner continuo (v1.0.3) ------------------------------------------------
    // El sensor se mantiene leyendo SIEMPRE en un loop de fondo en vez de armarse por
    // request. Ver FingerprintScanner: elimina la zona muerta entre polls de /identify.

    /// <summary>Kill switch. false = vuelve al camino viejo (capturar por request).</summary>
    public bool ContinuousScan { get; init; } = true;

    /// <summary>Cada cuanto el loop pregunta si hay dedo. 200ms perdia toques cortos.</summary>
    public int ScanPollMs { get; init; } = 40;

    /// <summary>Cuanto vale un dedo bufferado. Mas viejo que esto no marca (dedo rancio).</summary>
    public int ProbeFreshnessMs { get; init; } = 2500;

    /// <summary>Pausa tras leer un dedo, para no republicar el mismo dedo apoyado.</summary>
    public int SameFingerDebounceMs { get; init; } = 400;

    /// <summary>
    /// Solo dev: deja que MockDevice invente un dedo cada 2s para probar el flujo sin
    /// hardware. APAGADO por defecto a proposito. Si el SDK real no abre (caso clasico:
    /// el agente corriendo como servicio en Session 0 no ve el USB) DeviceFactory cae a
    /// MockDevice, que ademas reporta connected. Con el scanner continuo + auto-dedo eso
    /// registraria asistencias fantasma cada 2s sin que nadie apoye nada.
    /// </summary>
    public bool MockAutoFinger { get; init; } = false;

    // -- Reconexion del lector (v1.1) ---------------------------------------------

    /// <summary>Cada cuantos segundos se reintenta abrir el lector cuando esta cerrado.</summary>
    public int ReconnectSeconds { get; init; } = 2;

    /// <summary>
    /// Cada cuanto se confirma que el lector SIGUE conectado. Hace falta porque
    /// AcquireFingerprint devuelve el mismo codigo (-8) para "no hay dedo" que para un
    /// handle muerto: sin este chequeo, un lector desenchufado se ve igual que un lector
    /// esperando un dedo.
    /// </summary>
    public int PresenceCheckSeconds { get; init; } = 30;

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

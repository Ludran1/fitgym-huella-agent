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

    /// <summary>
    /// Umbral de DBIdentify del SDK, escala 0-1000. Por debajo, el match se descarta.
    ///
    /// Estuvo en 0 hasta el 18-sep, que en la practica dejaba mandando al piso interno del
    /// SDK (70). El 300 sale de las primeras mediciones reales: 13 lecturas de dedos
    /// enrolados dieron entre **493 y 874**, y 11 lecturas de un dedo ajeno se rechazaron
    /// todas. O sea, 300 queda comodo por debajo del peor acierto y muy por encima del
    /// piso viejo. Subirlo mas exige medir mas dedos y mas gente antes.
    /// </summary>
    public int IdentifyThreshold { get; init; } = 300;

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

    /// <summary>
    /// Puntaje minimo que tienen que sacar entre si las 3 capturas del enrolado (1:1,
    /// escala 0-1000). Por debajo se rechaza y se pide repetir.
    ///
    /// Una huella mal enrolada no se arregla despues: esa persona lee mal todos los dias.
    /// El 300 arranca igual que IdentifyThreshold; con los puntajes que ahora quedan en el
    /// log de cada enrolado se puede subir o bajar con datos.
    /// </summary>
    public int EnrollMinScore { get; init; } = 300;

    // -- Portero autonomo (v1.2) --------------------------------------------------

    /// <summary>
    /// true = el AGENTE decide y abre, sin navegador: dedo → 1:N local → registrar_acceso
    /// (el juez unico en Supabase) → pulso al rele. Con esto la puerta funciona con Chrome
    /// cerrado, que es el limite que teniamos hasta el 18-sep.
    ///
    /// Mientras este prendido, el panel NO debe pedir identify: /health anuncia
    /// `decide: "agente"` y el listener del navegador se apaga solo. Si los dos preguntaran
    /// se robarian el dedo y la puerta abriria dos veces.
    /// </summary>
    public bool AutoDecide { get; init; } = false;

    /// <summary>Anti-rebote del portero: el mismo dedo apoyado no vuelve a pedir veredicto.</summary>
    public int AutoDecideDedupSeconds { get; init; } = 6;

    // -- Reconexion del lector (v1.1) ---------------------------------------------

    /// <summary>Cada cuantos segundos se reintenta abrir el lector cuando esta cerrado.</summary>
    public int ReconnectSeconds { get; init; } = 2;

    /// <summary>
    /// Cada cuanto se confirma que el lector SIGUE conectado. Hace falta porque
    /// AcquireFingerprint devuelve el mismo codigo (-8) para "no hay dedo" que para un
    /// handle muerto: sin este chequeo, un lector desenchufado se ve igual que un lector
    /// esperando un dedo.
    /// </summary>
    public int PresenceCheckSeconds { get; init; } = 10;

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

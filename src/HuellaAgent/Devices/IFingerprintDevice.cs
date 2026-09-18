using HuellaAgent.Storage;

namespace HuellaAgent.Devices;

/// <summary>Captura cruda de un dedo: template base64 + calidad (0-100).</summary>
public sealed record CaptureResult(string Template, int Quality);

/// <summary>Match de identify: uid interno del SDK + score de similitud.</summary>
public sealed record IdentifyMatch(int Uid, int Score);

/// <summary>No hubo dedo en la ventana de tiempo → HTTP 408.</summary>
public sealed class NoFingerException : Exception { }

/// <summary>El lector no esta disponible (desconectado / SDK no init) → HTTP 503.</summary>
public sealed class DeviceUnavailableException : Exception
{
    public DeviceUnavailableException(string msg) : base(msg) { }
}

/// <summary>
/// Abstraccion del lector. Dos implementaciones: ZkfpDevice (Windows, SDK real) y
/// MockDevice (Linux/CI/dev). Permite construir y testear TODA la capa HTTP sin hardware.
/// </summary>
public interface IFingerprintDevice
{
    bool IsConnected { get; }
    string? DeviceName { get; }

    /// <summary>Desde cuando esta abierto el lector. null = cerrado.</summary>
    DateTime? ConnectedSinceUtc { get; }

    /// <summary>
    /// Ultimo error del lector, para /health. Antes esto se perdia: el agente caia al
    /// MockDevice en silencio y /health seguia diciendo reader "connected" (08 y 10-sep).
    /// </summary>
    string? LastError { get; }

    /// <summary>
    /// Intenta (re)abrir el lector si esta cerrado. true = quedo abierto. La llama el
    /// scanner cuando IsConnected es false; trae su propio freno entre intentos.
    /// </summary>
    bool TryReconnect();

    /// <summary>Captura un dedo. Lanza NoFingerException si no hay dedo en timeout.</summary>
    Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct);

    /// <summary>Fusiona 3 capturas en 1 template enrolado (DBMerge del SDK).</summary>
    string Merge(string template1, string template2, string template3);

    /// <summary>
    /// Carga <paramref name="db"/> en la DB en memoria, captura un dedo y hace 1:N.
    /// Devuelve el match, o null si hubo dedo pero sin coincidencia (→ 404).
    /// Lanza NoFingerException si no hubo dedo (→ 408).
    /// </summary>
    Task<IdentifyMatch?> IdentifyAsync(IReadOnlyList<StoredTemplate> db, int timeoutSeconds, CancellationToken ct);

    // ── Primitivas del scanner continuo (v1.0.3) ────────────────────────────────
    // El modelo viejo era "capturar bajo demanda": cada POST /identify armaba el sensor,
    // esperaba hasta 4s y lo desarmaba. Entre respuesta y siguiente request el lector
    // quedaba CIEGO (~1.1-1.5s de cada ~5.2s en el kiosko) → el dedo apoyado justo ahi
    // no se leia: el "a veces agarra, a veces no". Ahora FingerprintScanner mantiene el
    // sensor leyendo SIEMPRE con estas dos primitivas, y /identify solo consume el buffer.

    /// <summary>
    /// Intenta leer un dedo AHORA, sin bloquear. null = no hay dedo apoyado en este instante.
    /// La llama el loop del scanner cada pocos ms; no debe esperar ni dormir.
    /// </summary>
    CaptureResult? TryCapture();

    /// <summary>
    /// 1:N de un template ya capturado contra los templates del tenant. null = sin match.
    /// <paramref name="dbKey"/> permite cachear la DB en memoria del SDK entre llamadas: se
    /// reconstruye SOLO si la clave cambio (antes se hacia DBInit + N×DBAdd + DBFree en CADA
    /// poll, costo lineal con la cantidad de socios, ~cada 5s y tirandola a la basura).
    /// La clave debe identificar el CONTENIDO de <paramref name="db"/> — el endpoint usa
    /// "{tenantId}:{store.Version}", asi cambia tanto al enrolar como al cambiar de tenant.
    /// Vacia = no cachear (rearmar siempre).
    /// </summary>
    IdentifyMatch? Identify(string probeTemplate, IReadOnlyList<StoredTemplate> db, string dbKey);
}

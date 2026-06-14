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
}

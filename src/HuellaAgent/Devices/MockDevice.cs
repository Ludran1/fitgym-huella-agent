using System.Text;
using HuellaAgent.Config;
using HuellaAgent.Storage;

namespace HuellaAgent.Devices;

/// <summary>
/// Lector simulado para Linux/CI/dev (sin SDK ni hardware). Permite probar el flujo
/// end-to-end: capturar 3 veces, enrolar, y que identify "reconozca" al ultimo enrolado.
/// NO simula biometria real (FAR/FRR) - eso es exclusivo de ZkfpDevice (R1, Fase 6).
/// </summary>
public sealed class MockDevice : IFingerprintDevice
{
    private readonly bool _autoFinger;
    private DateTime _lastMockFinger = DateTime.UtcNow;

    public MockDevice(AgentConfig? cfg = null) => _autoFinger = cfg?.MockAutoFinger ?? false;

    public bool IsConnected => true;
    public string? DeviceName => "MockDevice (SLK20R simulado)";
    public DateTime? ConnectedSinceUtc { get; } = DateTime.UtcNow;
    public string? LastError => null;
    public bool TryReconnect() => true;

    public async Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
    {
        await Task.Delay(120, ct);   // simula el barrido del dedo
        return NewCapture();
    }

    /// <summary>
    /// Sin hardware no hay dedo apoyado, asi que por defecto NO inventa ninguno: devuelve
    /// null y el scanner continuo no publica nada.
    ///
    /// Esto es deliberado y es una salvaguarda, no una limitacion. DeviceFactory cae a
    /// MockDevice cuando el SDK real no abre - el caso clasico es el agente corriendo como
    /// servicio en Session 0, que no ve el USB - y MockDevice reporta IsConnected=true, asi
    /// que /health miente. Si ademas inventara dedos, el scanner continuo registraria una
    /// asistencia fantasma del ultimo socio enrolado cada 2 segundos, sin que nadie toque
    /// el lector. Para probar el flujo en dev: Agent:MockAutoFinger=true.
    /// </summary>
    public CaptureResult? TryCapture()
    {
        if (!_autoFinger) return null;
        var now = DateTime.UtcNow;
        if ((now - _lastMockFinger).TotalMilliseconds < 2000) return null;
        _lastMockFinger = now;
        return NewCapture();
    }

    private static CaptureResult NewCapture()
    {
        var raw = Encoding.UTF8.GetBytes($"MOCK-CAP::{Guid.NewGuid():N}");
        return new CaptureResult(Convert.ToBase64String(raw), Quality: 85);
    }

    public string Merge(string template1, string template2, string template3)
    {
        // El SDK fusiona 3->1; aca solo dejamos un template determinista del conjunto.
        var raw = Encoding.UTF8.GetBytes($"MOCK-MERGED::{template1.Length}.{template2.Length}.{template3.Length}");
        return Convert.ToBase64String(raw);
    }

    public IdentifyMatch? Identify(string probeTemplate, IReadOnlyList<StoredTemplate> db, string dbKey)
    {
        if (db.Count == 0) return null;                  // dedo presente, sin match -> 404
        return new IdentifyMatch(db[^1].Uid, Score: 95); // "reconoce" al ultimo enrolado
    }

    public async Task<IdentifyMatch?> IdentifyAsync(
        IReadOnlyList<StoredTemplate> db, int timeoutSeconds, CancellationToken ct)
    {
        await Task.Delay(120, ct);
        return Identify(probeTemplate: "", db, dbKey: "");
    }
}

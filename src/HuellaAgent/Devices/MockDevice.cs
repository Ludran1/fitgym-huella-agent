using System.Text;
using HuellaAgent.Storage;

namespace HuellaAgent.Devices;

/// <summary>
/// Lector simulado para Linux/CI/dev (sin SDK ni hardware). Permite probar el flujo
/// end-to-end: capturar 3 veces, enrolar, y que identify "reconozca" al ultimo enrolado.
/// NO simula biometria real (FAR/FRR) — eso es exclusivo de ZkfpDevice (R1, Fase 6).
/// </summary>
public sealed class MockDevice : IFingerprintDevice
{
    public bool IsConnected => true;
    public string? DeviceName => "MockDevice (SLK20R simulado)";

    public async Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
    {
        await Task.Delay(120, ct);   // simula el barrido del dedo
        var raw = Encoding.UTF8.GetBytes($"MOCK-CAP::{Guid.NewGuid():N}");
        return new CaptureResult(Convert.ToBase64String(raw), Quality: 85);
    }

    public string Merge(string template1, string template2, string template3)
    {
        // El SDK fusiona 3→1; aca solo dejamos un template determinista del conjunto.
        var raw = Encoding.UTF8.GetBytes($"MOCK-MERGED::{template1.Length}.{template2.Length}.{template3.Length}");
        return Convert.ToBase64String(raw);
    }

    public async Task<IdentifyMatch?> IdentifyAsync(
        IReadOnlyList<StoredTemplate> db, int timeoutSeconds, CancellationToken ct)
    {
        await Task.Delay(120, ct);
        if (db.Count == 0) return null;                  // dedo presente, sin match → 404
        var last = db[^1];                               // "reconoce" al ultimo enrolado
        return new IdentifyMatch(last.Uid, Score: 95);
    }
}

using HuellaAgent.Devices;
using HuellaAgent.Storage;

namespace HuellaAgent.Tests;

/// <summary>Lector controlable para fijar cada codigo HTTP del contrato sin hardware.</summary>
public sealed class FakeDevice : IFingerprintDevice
{
    public bool IsConnected { get; set; } = true;
    public string? DeviceName { get; set; } = "FakeDevice";

    public CaptureResult Capture { get; set; } = new("CAP-AAA", 90);
    public bool CaptureNoFinger { get; set; }

    public bool IdentifyNoFinger { get; set; }
    public bool IdentifyNoMatch { get; set; }
    public int? IdentifyMatchUid { get; set; }
    public int IdentifyScore { get; set; } = 99;

    public Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
        => CaptureNoFinger ? throw new NoFingerException() : Task.FromResult(Capture);

    public string Merge(string t1, string t2, string t3) => $"MERGED::{t1}.{t2}.{t3}";

    public Task<IdentifyMatch?> IdentifyAsync(IReadOnlyList<StoredTemplate> db, int timeoutSeconds, CancellationToken ct)
    {
        if (IdentifyNoFinger) throw new NoFingerException();
        if (IdentifyNoMatch) return Task.FromResult<IdentifyMatch?>(null);
        if (IdentifyMatchUid is int uid) return Task.FromResult<IdentifyMatch?>(new IdentifyMatch(uid, IdentifyScore));
        return Task.FromResult<IdentifyMatch?>(db.Count == 0 ? null : new IdentifyMatch(db[^1].Uid, IdentifyScore));
    }
}

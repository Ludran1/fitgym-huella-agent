using HuellaAgent.Devices;
using HuellaAgent.Storage;

namespace HuellaAgent.Tests;

/// <summary>Lector controlable para fijar cada codigo HTTP del contrato sin hardware.</summary>
public sealed class FakeDevice : IFingerprintDevice
{
    public bool IsConnected { get; set; } = true;
    public string? DeviceName { get; set; } = "FakeDevice";
    public DateTime? ConnectedSinceUtc { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }

    /// <summary>Cuantas veces le pidieron reconectar.</summary>
    public int ReconnectCalls;

    /// <summary>Si esta en true, reconectar deja el lector conectado.</summary>
    public bool ReconnectFunciona { get; set; } = true;

    public bool TryReconnect()
    {
        Interlocked.Increment(ref ReconnectCalls);
        if (!ReconnectFunciona) return false;
        IsConnected = true;
        ConnectedSinceUtc ??= DateTime.UtcNow;
        LastError = null;
        return true;
    }

    /// <summary>El lector se cae: lo que simula un USB desenchufado.</summary>
    public void Desconectar(string motivo = "lector desenchufado")
    {
        IsConnected = false;
        ConnectedSinceUtc = null;
        LastError = motivo;
    }

    /// <summary>Si esta en true, TryCapture tira DeviceUnavailableException.</summary>
    public bool TryCaptureFalla { get; set; }

    public CaptureResult Capture { get; set; } = new("CAP-AAA");
    public bool CaptureNoFinger { get; set; }

    public bool IdentifyNoFinger { get; set; }
    public bool IdentifyNoMatch { get; set; }
    public int? IdentifyMatchUid { get; set; }
    public int IdentifyScore { get; set; } = 99;

    /// <summary>Cuantas veces se rearmo la DB del SDK (para testear el cache por dbKey).</summary>
    public int DbRebuilds { get; private set; }
    private string _lastDbKey = "";

    /// <summary>Cuantas veces el scanner pregunto si hay dedo.</summary>
    public int TryCaptureCalls;

    public Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
        => CaptureNoFinger ? throw new NoFingerException() : Task.FromResult(Capture);

    public CaptureResult? TryCapture()
    {
        Interlocked.Increment(ref TryCaptureCalls);
        if (TryCaptureFalla) throw new DeviceUnavailableException("el lector ya no aparece");
        // IdentifyNoFinger modela "nadie apoya el dedo": el scanner nunca publica nada.
        return IdentifyNoFinger ? null : Capture;
    }

    public string Merge(string t1, string t2, string t3) => $"MERGED::{t1}.{t2}.{t3}";

    /// <summary>Puntaje 1:1 que devuelve el doble; los tests lo mueven para probar el enrolado flojo.</summary>
    public int MatchScore { get; set; } = 900;
    public int Match(string template1, string template2) => MatchScore;

    public IdentifyMatch? Identify(string probeTemplate, IReadOnlyList<StoredTemplate> db, string dbKey)
    {
        // Simula el cache de la DB en memoria del SDK: solo se rearma si cambio la clave.
        if (dbKey.Length == 0 || dbKey != _lastDbKey)
        {
            DbRebuilds++;
            _lastDbKey = dbKey;
        }

        if (IdentifyNoMatch) return null;
        if (IdentifyMatchUid is int uid) return new IdentifyMatch(uid, IdentifyScore);
        return db.Count == 0 ? null : new IdentifyMatch(db[^1].Uid, IdentifyScore);
    }

    public Task<IdentifyMatch?> IdentifyAsync(IReadOnlyList<StoredTemplate> db, int timeoutSeconds, CancellationToken ct)
    {
        if (IdentifyNoFinger) throw new NoFingerException();
        return Task.FromResult(Identify(probeTemplate: "", db, dbKey: ""));
    }
}

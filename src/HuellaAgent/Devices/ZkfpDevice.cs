using System.Runtime.Versioning;
using HuellaAgent.Storage;

namespace HuellaAgent.Devices;

/// <summary>
/// Lector REAL via ZKFinger SDK (libzkfp). Solo se instancia en Windows con el SDK + DLLs.
///
/// 🔴 Fase 6 (necesita SDK + PC Windows + SLK20R fisico):
///   - Verificar las firmas de ZkfpNative contra el header del SDK.
///   - R1 CRITICO: calibrar IdentifyThreshold (FAR/FRR) con dedos reales antes de piloto.
/// La logica de flujo (capturar con timeout, merge 3→1, cargar DB + 1:N) ya esta escrita;
/// en Windows solo hay que soltar libzkfp.dll/libzkfpcsharp.dll y compilar.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ZkfpDevice : IFingerprintDevice, IDisposable
{
    // Tamaños tipicos del SDK ZKFinger (ajustar si el header difiere).
    private const int ImageBufferSize = 300 * 1024;
    private const int TemplateBufferSize = 2048;

    private readonly IntPtr _device;
    private bool _initialized;

    public ZkfpDevice()
    {
        if (ZkfpNative.ZKFPM_Init() != 0)
            throw new DeviceUnavailableException("ZKFPM_Init fallo (SDK no disponible)");
        _initialized = true;

        if (ZkfpNative.ZKFPM_GetDeviceCount() <= 0)
            throw new DeviceUnavailableException("No se detecto ningun lector USB");

        _device = ZkfpNative.ZKFPM_OpenDevice(0);
        if (_device == IntPtr.Zero)
            throw new DeviceUnavailableException("ZKFPM_OpenDevice fallo");
    }

    public bool IsConnected => _initialized && _device != IntPtr.Zero;
    public string? DeviceName => "ZKTeco SLK20R";

    public async Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var image = new byte[ImageBufferSize];
            var template = new byte[TemplateBufferSize];
            uint cbTemplate = (uint)template.Length;

            int rc = ZkfpNative.ZKFPM_AcquireFingerprint(
                _device, image, (uint)image.Length, template, ref cbTemplate);
            if (rc == 0)
            {
                var b64 = Convert.ToBase64String(template, 0, (int)cbTemplate);
                return new CaptureResult(b64, Quality: 100);
            }
            await Task.Delay(200, ct);   // sin dedo aun: reintenta
        }
        throw new NoFingerException();
    }

    public string Merge(string template1, string template2, string template3)
    {
        IntPtr db = ZkfpNative.ZKFPM_DBInit();
        if (db == IntPtr.Zero) throw new DeviceUnavailableException("ZKFPM_DBInit fallo");
        try
        {
            var reg = new byte[TemplateBufferSize];
            uint cbReg = (uint)reg.Length;
            int rc = ZkfpNative.ZKFPM_DBMerge(
                db,
                Convert.FromBase64String(template1),
                Convert.FromBase64String(template2),
                Convert.FromBase64String(template3),
                reg, ref cbReg);
            if (rc != 0) throw new InvalidOperationException($"ZKFPM_DBMerge fallo (rc={rc})");
            return Convert.ToBase64String(reg, 0, (int)cbReg);
        }
        finally { ZkfpNative.ZKFPM_DBFree(db); }
    }

    public async Task<IdentifyMatch?> IdentifyAsync(
        IReadOnlyList<StoredTemplate> dbItems, int timeoutSeconds, CancellationToken ct)
    {
        IntPtr db = ZkfpNative.ZKFPM_DBInit();
        if (db == IntPtr.Zero) throw new DeviceUnavailableException("ZKFPM_DBInit fallo");
        try
        {
            ZkfpNative.ZKFPM_DBClear(db);
            foreach (var item in dbItems)
            {
                var bytes = Convert.FromBase64String(item.Template);
                ZkfpNative.ZKFPM_DBAdd(db, (uint)item.Uid, bytes, (uint)bytes.Length);
            }

            var probe = await CaptureAsync(timeoutSeconds, ct);   // lanza NoFingerException si no hay dedo
            var probeBytes = Convert.FromBase64String(probe.Template);

            uint fid = 0, score = 0;
            int rc = ZkfpNative.ZKFPM_DBIdentify(db, probeBytes, (uint)probeBytes.Length, ref fid, ref score);
            if (rc != 0 || score == 0) return null;               // dedo sin match → 404
            return new IdentifyMatch((int)fid, (int)score);
        }
        finally { ZkfpNative.ZKFPM_DBFree(db); }
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero) ZkfpNative.ZKFPM_CloseDevice(_device);
        if (_initialized) ZkfpNative.ZKFPM_Terminate();
    }
}

#if ZKFP
using System.Runtime.Versioning;
using HuellaAgent.Config;
using HuellaAgent.Storage;
using libzkfpcsharp;   // wrapper oficial del ZKFinger SDK 5.3.0.33 (sdk/win-x64/libzkfpcsharp.dll)

namespace HuellaAgent.Devices;

/// <summary>
/// Lector REAL via ZKFinger SDK (clase zkfp2 del wrapper libzkfpcsharp). Solo se compila
/// al publicar para win-x64 (define ZKFP en el csproj) y solo se instancia en Windows con
/// UseRealDevice=true. La API replica el demo oficial (C#/Demo2/Form1.cs del SDK).
///
/// 🔴 Fase 6 (necesita el lector fisico): el codigo ya calza con la API del SDK; al correr
///   en la PC Windows con el SLK20R hay que calibrar Agent:IdentifyThreshold (R1, FAR/FRR).
/// Requisitos de runtime: correr setup.exe del SDK (instala el driver USB + libzkfp.dll
/// nativo que este wrapper P/Invoca).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ZkfpDevice : IFingerprintDevice, IDisposable
{
    private const int TemplateSize = 2048;

    private readonly int _threshold;
    private readonly IntPtr _device;
    private readonly byte[] _imageBuffer;
    private bool _initialized;

    public ZkfpDevice(AgentConfig cfg)
    {
        _threshold = cfg.IdentifyThreshold;

        if (zkfp2.Init() != zkfp.ZKFP_ERR_OK)
            throw new DeviceUnavailableException("zkfp2.Init fallo (¿setup.exe del SDK instalado?)");
        _initialized = true;

        if (zkfp2.GetDeviceCount() <= 0)
        {
            zkfp2.Terminate();
            throw new DeviceUnavailableException("No hay lector USB conectado");
        }

        _device = zkfp2.OpenDevice(0);
        if (_device == IntPtr.Zero)
        {
            zkfp2.Terminate();
            throw new DeviceUnavailableException("zkfp2.OpenDevice fallo");
        }

        // El buffer de imagen se dimensiona con el ancho/alto del device (param 1 y 2).
        int width = 0, height = 0;
        byte[] paramBuf = new byte[4];
        int size = 4;
        zkfp2.GetParameters(_device, 1, paramBuf, ref size);
        zkfp2.ByteArray2Int(paramBuf, ref width);
        size = 4;
        zkfp2.GetParameters(_device, 2, paramBuf, ref size);
        zkfp2.ByteArray2Int(paramBuf, ref height);
        _imageBuffer = new byte[Math.Max(1, width * height)];
    }

    public bool IsConnected => _initialized && _device != IntPtr.Zero;
    public string? DeviceName => "ZKTeco SLK20R";

    public async Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var template = new byte[TemplateSize];
            int cb = TemplateSize;
            int rc = zkfp2.AcquireFingerprint(_device, _imageBuffer, template, ref cb);
            if (rc == zkfp.ZKFP_ERR_OK)
                return new CaptureResult(zkfp2.BlobToBase64(template, cb), Quality: 100);
            await Task.Delay(200, ct);   // sin dedo aun
        }
        throw new NoFingerException();
    }

    public string Merge(string template1, string template2, string template3)
    {
        IntPtr db = zkfp2.DBInit();
        if (db == IntPtr.Zero) throw new DeviceUnavailableException("zkfp2.DBInit fallo");
        try
        {
            byte[] reg = new byte[TemplateSize];
            int cb = TemplateSize;
            int rc = zkfp2.DBMerge(db,
                zkfp2.Base64ToBlob(template1),
                zkfp2.Base64ToBlob(template2),
                zkfp2.Base64ToBlob(template3),
                reg, ref cb);
            if (rc != zkfp.ZKFP_ERR_OK)
                throw new InvalidOperationException($"zkfp2.DBMerge fallo (rc={rc})");
            return zkfp2.BlobToBase64(reg, cb);
        }
        finally { zkfp2.DBFree(db); }
    }

    public async Task<IdentifyMatch?> IdentifyAsync(
        IReadOnlyList<StoredTemplate> items, int timeoutSeconds, CancellationToken ct)
    {
        IntPtr db = zkfp2.DBInit();
        if (db == IntPtr.Zero) throw new DeviceUnavailableException("zkfp2.DBInit fallo");
        try
        {
            // Carga la DB en memoria con los templates del tenant (fid = uid).
            foreach (var it in items)
                zkfp2.DBAdd(db, it.Uid, zkfp2.Base64ToBlob(it.Template));

            var probe = await CaptureAsync(timeoutSeconds, ct);   // NoFingerException si no hay dedo
            byte[] probeBlob = zkfp2.Base64ToBlob(probe.Template);

            int fid = 0, score = 0;
            int rc = zkfp2.DBIdentify(db, probeBlob, ref fid, ref score);
            if (rc != zkfp.ZKFP_ERR_OK) return null;   // dedo sin coincidencia → 404
            if (score < _threshold) return null;       // bajo el umbral calibrado (R1)
            return new IdentifyMatch(fid, score);
        }
        finally { zkfp2.DBFree(db); }
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero) zkfp2.CloseDevice(_device);
        if (_initialized) zkfp2.Terminate();
    }
}
#endif

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
/// Requisitos de runtime: correr setup.exe del SDK (instala el driver USB + libzkfp.dll
/// nativo que este wrapper P/Invoca).
///
/// Concurrencia: FingerprintScanner llama TryCapture() desde su thread de fondo mientras
/// los requests HTTP llaman Identify()/Merge(). Son handles nativos distintos (_device vs
/// la DB), pero el SDK no documenta thread-safety → todo lo nativo va bajo _sdkLock.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ZkfpDevice : IFingerprintDevice, IDisposable
{
    private const int TemplateSize = 2048;

    private readonly int _threshold;
    private readonly IntPtr _device;
    private readonly byte[] _imageBuffer;
    private readonly object _sdkLock = new();
    private bool _initialized;

    // DB en memoria del SDK, CACHEADA entre llamadas. Antes cada identify hacia
    // DBInit + N×DBAdd + DBFree (O(socios) por poll, ~cada 5s, tirandola a la basura).
    // Ahora se reconstruye solo cuando cambia _dbKey ("{tenant}:{version}" → cambia al
    // enrolar o al identificar contra otro tenant).
    private IntPtr _db = IntPtr.Zero;
    private string _dbKey = "";

    private readonly TimeSpan _presencia;
    private DateTime _ultimoChequeo = DateTime.UtcNow;
    private readonly string? _modelo;
    private readonly string? _serie;

    public ZkfpDevice(AgentConfig cfg)
    {
        _threshold = cfg.IdentifyThreshold;
        _presencia = TimeSpan.FromSeconds(Math.Max(5, cfg.PresenceCheckSeconds));

        // Primero lo barato y lo que no miente: si Windows no ve ningun lector ZKTeco, no
        // tiene sentido molestar al SDK, que ademas podria abrir un lector fantasma con su
        // estado cacheado y dejar a /health diciendo "connected" sin hardware detras.
        if (!UsbPresencia.HayLector())
            throw new DeviceUnavailableException("Windows no ve ningun lector ZKTeco enchufado");

        // ALREADY_INIT (1) NO es un error: pasa al reabrir el lector dentro del mismo
        // proceso si un Terminate anterior no llego a correr. Antes esto tiraba
        // "zkfp2.Init fallo" y el agente se quedaba sin lector hasta reiniciarse.
        int rcInit = zkfp2.Init();
        if (rcInit != zkfp.ZKFP_ERR_OK && rcInit != zkfp.ZKFP_ERR_ALREADY_INIT)
            throw new DeviceUnavailableException($"zkfp2.Init fallo (rc={rcInit}; ¿setup.exe del SDK instalado?)");
        _initialized = true;

        int cuantos = zkfp2.GetDeviceCount();
        if (cuantos <= 0)
        {
            zkfp2.Terminate();
            throw new DeviceUnavailableException($"No hay lector USB conectado (GetDeviceCount={cuantos})");
        }

        _device = zkfp2.OpenDevice(0);
        if (_device == IntPtr.Zero)
        {
            zkfp2.Terminate();
            // Causa mas comun: OTRO proceso ya tiene el lector (el demo del SDK, otra
            // instancia del agente). El lector es exclusivo entre procesos.
            throw new DeviceUnavailableException("zkfp2.OpenDevice fallo (¿otro programa esta usando el lector?)");
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

        // Modelo REAL del lector (parametro 1102), en vez de asumir SLK20R: el mismo SDK
        // maneja ZK9500, ZK6500 y ZK8500R, y al diagnosticar conviene saber cual hay.
        _modelo = LeerTexto(1102);
        _serie = LeerTexto(1103);
        ConnectedSinceUtc = DateTime.UtcNow;
    }

    public bool IsConnected => _initialized && _device != IntPtr.Zero;
    public DateTime? ConnectedSinceUtc { get; private set; }
    public string? LastError { get; private set; }
    public string? DeviceName =>
        string.IsNullOrWhiteSpace(_modelo) ? "ZKTeco (modelo desconocido)" : $"ZKTeco {_modelo}";

    /// <summary>Numero de serie del lector, si el SDK lo entrega (para /health).</summary>
    public string? Serie => _serie;

    /// <summary>Ya esta abierto: la reapertura la maneja ReconnectingDevice creando otro.</summary>
    public bool TryReconnect() => IsConnected;

    private string? LeerTexto(int code)
    {
        try
        {
            var buf = new byte[256];
            int size = buf.Length;
            if (zkfp2.GetParameters(_device, code, buf, ref size) != zkfp.ZKFP_ERR_OK) return null;
            var txt = System.Text.Encoding.ASCII.GetString(buf, 0, Math.Max(0, Math.Min(size, buf.Length)))
                .Trim('\0', ' ');
            return string.IsNullOrWhiteSpace(txt) ? null : txt;
        }
        catch { return null; }
    }

    /// <summary>Una sola lectura, sin esperar. null = no hay dedo en este instante.</summary>
    public CaptureResult? TryCapture()
    {
        lock (_sdkLock)
        {
            if (!IsConnected) throw new DeviceUnavailableException("lector desconectado");
            var template = new byte[TemplateSize];
            int cb = TemplateSize;
            int rc = zkfp2.AcquireFingerprint(_device, _imageBuffer, template, ref cb);
            if (rc == zkfp.ZKFP_ERR_OK)
            {
                _ultimoChequeo = DateTime.UtcNow;
                LastError = null;
                return new CaptureResult(zkfp2.BlobToBase64(template, cb), Quality: 100);
            }

            // Cualquier rc que NO sea "sin dedo" es el lector quejandose: que lo recicle
            // ReconnectingDevice en vez de seguir leyendo un handle muerto.
            if (rc != zkfp.ZKFP_ERR_CAPTURE)
                throw new DeviceUnavailableException($"AcquireFingerprint rc={rc}");

            // rc = -8 significa "sin dedo", PERO tambien es lo que devuelve un handle ya
            // cerrado (verificado el 16-sep): no distingue "nadie apoyo el dedo" de
            // "desenchufaron el lector". Por eso, cada PresenceCheckSeconds se pregunta
            // aparte si el lector sigue ahi.
            if (DateTime.UtcNow - _ultimoChequeo > _presencia)
            {
                _ultimoChequeo = DateTime.UtcNow;
                // Se le pregunta a WINDOWS, no al SDK: con el lector desenchufado el SDK
                // sigue diciendo que hay uno (GetDeviceCount=1 y AcquireFingerprint=-8),
                // verificado el 18-sep. El chequeo del SDK queda igual, de respaldo.
                if (!UsbPresencia.HayLector())
                    throw new DeviceUnavailableException("Windows ya no ve el lector (desenchufado)");
                int cuantos = zkfp2.GetDeviceCount();
                if (cuantos <= 0)
                    throw new DeviceUnavailableException($"el lector ya no aparece (GetDeviceCount={cuantos})");
            }
            return null;
        }
    }

    public async Task<CaptureResult> CaptureAsync(int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var r = TryCapture();
            if (r is not null) return r;
            await Task.Delay(40, ct);   // sin dedo aun (antes 200ms: un toque corto se escapaba)
        }
        throw new NoFingerException();
    }

    public string Merge(string template1, string template2, string template3)
    {
        lock (_sdkLock)
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
    }

    /// <summary>1:N contra la DB cacheada del SDK (se rearma solo si cambio dbKey).</summary>
    public IdentifyMatch? Identify(string probeTemplate, IReadOnlyList<StoredTemplate> items, string dbKey)
    {
        lock (_sdkLock)
        {
            SyncDbLocked(items, dbKey);
            if (_db == IntPtr.Zero || items.Count == 0) return null;

            byte[] probeBlob = zkfp2.Base64ToBlob(probeTemplate);
            int fid = 0, score = 0;
            int rc = zkfp2.DBIdentify(_db, probeBlob, ref fid, ref score);
            if (rc != zkfp.ZKFP_ERR_OK) return null;   // dedo sin coincidencia → 404
            if (score < _threshold) return null;       // bajo el umbral calibrado (R1)
            return new IdentifyMatch(fid, score);
        }
    }

    public async Task<IdentifyMatch?> IdentifyAsync(
        IReadOnlyList<StoredTemplate> items, int timeoutSeconds, CancellationToken ct)
    {
        // Camino legacy (ContinuousScan=false). Con el scanner prendido el endpoint no
        // pasa por aca: consume el buffer y llama Identify() directo.
        var probe = await CaptureAsync(timeoutSeconds, ct);   // NoFingerException si no hay dedo
        return Identify(probe.Template, items, dbKey: "");    // "" = rearmar siempre
    }

    // Rearma la DB del SDK solo si la clave cambio. dbKey vacia = forzar rearmado.
    // Debe llamarse SIEMPRE bajo _sdkLock.
    private void SyncDbLocked(IReadOnlyList<StoredTemplate> items, string dbKey)
    {
        if (_db != IntPtr.Zero && dbKey.Length > 0 && dbKey == _dbKey) return;

        if (_db != IntPtr.Zero) { zkfp2.DBFree(_db); _db = IntPtr.Zero; }
        _dbKey = "";

        var db = zkfp2.DBInit();
        if (db == IntPtr.Zero) throw new DeviceUnavailableException("zkfp2.DBInit fallo");

        // El rc de DBAdd se ignoraba: un template corrupto no entraba a la base y esa
        // persona no matcheaba NUNCA, sin que nadie se enterara. Ahora queda en /health.
        int rechazados = 0;
        foreach (var it in items)
            if (zkfp2.DBAdd(db, it.Uid, zkfp2.Base64ToBlob(it.Template)) != zkfp.ZKFP_ERR_OK) rechazados++;
        LastError = rechazados > 0
            ? $"{rechazados} de {items.Count} huellas no entraron en la base del lector (hay que volver a registrarlas)"
            : null;

        _db = db;
        _dbKey = dbKey;
    }

    public void Dispose()
    {
        lock (_sdkLock)
        {
            if (_db != IntPtr.Zero) { zkfp2.DBFree(_db); _db = IntPtr.Zero; }
            if (_device != IntPtr.Zero) zkfp2.CloseDevice(_device);
            if (_initialized) zkfp2.Terminate();
        }
    }
}
#endif

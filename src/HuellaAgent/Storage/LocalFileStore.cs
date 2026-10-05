using System.Text.Json;

namespace HuellaAgent.Storage;

/// <summary>
/// Store local en un JSON: { tenantId: [ {clienteId, uid, template}, ... ] }.
/// Sin DB, sin claves privilegiadas. Sobrevive reinicios; el identify 1:N corre offline.
/// Cuando se active la persistencia durable (RPCs huella_*), este store queda como cache.
/// </summary>
public sealed class LocalFileStore : ITemplateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // Cache en memoria del JSON. El identify pollea ~cada 4s y antes CADA poll leia el
    // archivo de disco + deserializaba TODOS los templates (costo lineal con la cantidad
    // de socios -> lento en gyms grandes). Solo este proceso escribe el archivo, asi que
    // la copia en memoria es la fuente de verdad: se llena en la 1ra lectura y se
    // refresca al escribir (enroll). _cache null = aun no cargado de disco.
    private Dictionary<string, List<StoredTemplate>>? _cache;

    // Sube en cada SaveAsync (enroll / carga inicial de templates). El device lo combina
    // con el tenant para saber si su DB en memoria del SDK sigue vigente.
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public LocalFileStore(string path) => _path = path;

    public async Task<IReadOnlyList<StoredTemplate>> LoadAsync(string tenantId)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();
            // Snapshot (copia) de la lista, no la referencia viva del cache: un enroll
            // concurrente muta la lista bajo _lock; si el identify iterara la referencia
            // compartida fuera del lock saltaria "Collection was modified".
            return db.TryGetValue(tenantId, out var list)
                ? new List<StoredTemplate>(list)
                : new List<StoredTemplate>();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> SaveAsync(string tenantId, string clienteId, string template, int dedo = 1)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();
            if (!db.TryGetValue(tenantId, out var list))
            {
                list = new List<StoredTemplate>();
                db[tenantId] = list;
            }

            // La clave es la persona Y EL DEDO. Buscar solo por ClienteId —como hasta la
            // v1.3— hacia que enrolar el segundo dedo PISARA el primero: el socio terminaba
            // con una sola huella, justo lo contrario de lo que se le acababa de pedir.
            var existing = list.FirstOrDefault(t => t.ClienteId == clienteId && t.Dedo == dedo);
            int uid;
            if (existing is not null)
            {
                uid = existing.Uid;              // re-enroll: reusa uid → DB del SDK estable
                existing.Template = template;
            }
            else
            {
                uid = (list.Count == 0 ? 0 : list.Max(t => t.Uid)) + 1;
                list.Add(new StoredTemplate { ClienteId = clienteId, Uid = uid, Template = template, Dedo = dedo });
            }

            await WriteAllAsync(db);
            Interlocked.Increment(ref _version);   // invalida la DB cacheada del SDK
            return uid;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> ReemplazarAsync(string tenantId, IReadOnlyList<StoredTemplate> templates)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();

            // Desde la v1.6.1 esto corre en cada latido (5 min), no sólo al arrancar. Si el
            // servidor dice lo mismo que ya hay, no se toca nada: escribir el archivo y subir
            // la versión haría que el SDK rearme su base cada 5 minutos sin motivo.
            if (db.TryGetValue(tenantId, out var actual) && MismoContenido(actual, templates))
                return false;
            // Se conserva el uid QUE MANDA EL SERVIDOR, no uno local: asi dos PCs del mismo
            // gimnasio hablan de la misma persona con el mismo numero. El uid 0 o negativo
            // no existe del lado del servidor, pero si llegara, se le da uno nuevo para no
            // romper la DB en memoria del SDK.
            var lista = new List<StoredTemplate>();
            var siguiente = 1;
            foreach (var t in templates)
            {
                var uid = t.Uid > 0 ? t.Uid : siguiente;
                siguiente = Math.Max(siguiente, uid) + 1;
                // El dedo viaja: sin esto los dos dedos de una persona quedaban como "1" y
                // re-enrolar el principal podía pisar el de respaldo.
                lista.Add(new StoredTemplate { ClienteId = t.ClienteId, Uid = uid, Template = t.Template, Dedo = t.Dedo });
            }

            db[tenantId] = lista;
            await WriteAllAsync(db);
            Interlocked.Increment(ref _version);   // invalida la DB cacheada del SDK
            return true;
        }
        finally { _lock.Release(); }
    }

    private static bool MismoContenido(List<StoredTemplate> a, IReadOnlyList<StoredTemplate> b)
    {
        if (a.Count != b.Count) return false;
        static string Clave(StoredTemplate t) => $"{t.ClienteId}|{t.Uid}|{t.Dedo}|{t.Template}";
        return a.Select(Clave).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(b.Select(Clave).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public async Task<int> QuitarAsync(string tenantId, string clienteId, int? dedo = null)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();
            if (!db.TryGetValue(tenantId, out var list)) return 0;
            var sacadas = list.RemoveAll(t => t.ClienteId == clienteId && (dedo is null || t.Dedo == dedo));
            if (sacadas == 0) return 0;
            await WriteAllAsync(db);
            Interlocked.Increment(ref _version);   // la huella borrada deja de abrir YA
            return sacadas;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> CountAsync(string? tenantId = null)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();
            if (tenantId is null) return db.Values.Sum(l => l.Count);
            return db.TryGetValue(tenantId, out var lista) ? lista.Count : 0;
        }
        finally { _lock.Release(); }
    }

    public async Task OlvidarOtrosAsync(string tenantId)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();
            var sobran = db.Keys.Where(k => k != tenantId).ToList();
            if (sobran.Count == 0) return;

            foreach (var k in sobran) db.Remove(k);
            await WriteAllAsync(db);
            Interlocked.Increment(ref _version);   // invalida la DB cacheada del SDK
        }
        finally { _lock.Release(); }
    }
    // Devuelve el dict cacheado; la 1ra vez (o si se invalido) lo lee de disco. Corre
    // siempre bajo _lock (lo garantizan los callers), por eso no necesita sincronizacion
    // extra sobre _cache.
    private async Task<Dictionary<string, List<StoredTemplate>>> ReadAllAsync()
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(_path)) return _cache = new();
        await using var fs = File.OpenRead(_path);
        var data = await JsonSerializer.DeserializeAsync<Dictionary<string, List<StoredTemplate>>>(fs);
        return _cache = data ?? new();
    }

    private async Task WriteAllAsync(Dictionary<string, List<StoredTemplate>> db)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, db, JsonOpts);
        File.Move(tmp, _path, overwrite: true);   // escritura atomica
        _cache = db;                              // mantiene el cache caliente tras escribir
    }
}

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

    public async Task<int> SaveAsync(string tenantId, string clienteId, string template)
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

            var existing = list.FirstOrDefault(t => t.ClienteId == clienteId);
            int uid;
            if (existing is not null)
            {
                uid = existing.Uid;              // re-enroll: reusa uid → DB del SDK estable
                existing.Template = template;
            }
            else
            {
                uid = (list.Count == 0 ? 0 : list.Max(t => t.Uid)) + 1;
                list.Add(new StoredTemplate { ClienteId = clienteId, Uid = uid, Template = template });
            }

            await WriteAllAsync(db);
            Interlocked.Increment(ref _version);   // invalida la DB cacheada del SDK
            return uid;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> CountAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();
            return db.Values.Sum(l => l.Count);
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

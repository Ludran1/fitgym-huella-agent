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

    public LocalFileStore(string path) => _path = path;

    public async Task<IReadOnlyList<StoredTemplate>> LoadAsync(string tenantId)
    {
        await _lock.WaitAsync();
        try
        {
            var db = await ReadAllAsync();
            return db.TryGetValue(tenantId, out var list) ? list : new List<StoredTemplate>();
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

    private async Task<Dictionary<string, List<StoredTemplate>>> ReadAllAsync()
    {
        if (!File.Exists(_path)) return new();
        await using var fs = File.OpenRead(_path);
        var data = await JsonSerializer.DeserializeAsync<Dictionary<string, List<StoredTemplate>>>(fs);
        return data ?? new();
    }

    private async Task WriteAllAsync(Dictionary<string, List<StoredTemplate>> db)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, db, JsonOpts);
        File.Move(tmp, _path, overwrite: true);   // escritura atomica
    }
}

namespace HuellaAgent.Storage;

/// <summary>
/// Persistencia de templates por tenant. Reader-first usa LocalFileStore (archivo).
/// El uid se asigna server-side-equivalente aca: reutiliza el del cliente si ya existe,
/// si no max(uid)+1 del tenant (mismo criterio que la futura RPC huella_enroll).
/// </summary>
public interface ITemplateStore
{
    /// <summary>Todos los templates de un tenant (para cargar la DB en memoria del SDK).</summary>
    Task<IReadOnlyList<StoredTemplate>> LoadAsync(string tenantId);

    /// <summary>Guarda/actualiza el template de un cliente. Devuelve el uid (reusado o nuevo).</summary>
    Task<int> SaveAsync(string tenantId, string clienteId, string template);

    /// <summary>Total de templates cargados (todos los tenants) para /health.</summary>
    Task<int> CountAsync();

    /// <summary>
    /// Contador que sube en cada SaveAsync. El device lo usa (junto al tenant) como clave
    /// de cache de su DB en memoria del SDK: mientras no cambie, no hace falta rearmarla.
    /// </summary>
    long Version { get; }
}

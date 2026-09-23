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

    /// <summary>
    /// Reemplaza TODO el conjunto de un tenant por el que manda el servidor.
    ///
    /// POR QUE NO ALCANZA CON SaveAsync. SaveAsync FUSIONA: agrega o pisa, nunca saca. La
    /// carga inicial lo llamaba una vez por template, asi que bajaba las huellas que estan
    /// y no tocaba las que sobran. Si se borra un socio del panel, su fila se va de
    /// Supabase (huellas tiene on delete cascade) pero su huella queda en el templates.json
    /// de TODA PC que alguna vez la bajo, para siempre — y esa persona sigue abriendo la
    /// puerta. Cuantas mas computadoras paso el lector, mas lugares donde quedo.
    ///
    /// La lista que manda huella_templates es la verdad completa del gimnasio, asi que
    /// reemplazar es lo correcto. Solo se llama cuando el servidor contesto de verdad: si
    /// la consulta falla, TemplatesAsync devuelve null y no se toca nada — un corte de
    /// internet no puede vaciarle el lector a un gimnasio.
    /// </summary>
    Task ReemplazarAsync(string tenantId, IReadOnlyList<StoredTemplate> templates);

    /// <summary>Total de templates cargados (todos los tenants) para /health.</summary>
    Task<int> CountAsync();

    /// <summary>
    /// Contador que sube en cada SaveAsync. El device lo usa (junto al tenant) como clave
    /// de cache de su DB en memoria del SDK: mientras no cambie, no hace falta rearmarla.
    /// </summary>
    long Version { get; }
}

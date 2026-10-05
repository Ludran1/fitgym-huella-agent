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

    /// <summary>
    /// Guarda/actualiza el template de un DEDO de un cliente. Devuelve el uid (reusado o nuevo).
    /// </summary>
    /// <param name="dedo">1 = principal, 2 = respaldo. La clave es (cliente, dedo): sin eso,
    /// enrolar el segundo dedo pisaba el primero.</param>
    Task<int> SaveAsync(string tenantId, string clienteId, string template, int dedo = 1);

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
    /// <summary>true si la lista cambió. Si es igual no escribe ni sube la versión (la
    /// DB del SDK no se rearma por nada cada 5 minutos).</summary>
    Task<bool> ReemplazarAsync(string tenantId, IReadOnlyList<StoredTemplate> templates);

    /// <summary>Saca las huellas de una persona (un dedo, o todos si <paramref name="dedo"/>
    /// es null). Devuelve cuántas sacó.</summary>
    Task<int> QuitarAsync(string tenantId, string clienteId, int? dedo = null);

    /// <summary>
    /// Cuantos templates hay cargados para ESE gimnasio (null = todos los del archivo).
    ///
    /// Antes sumaba siempre todo el archivo. En una PC de un solo gimnasio da igual, y por
    /// eso no se noto: el 26-sep, despues de re-vincular la PC de desarrollo de Villa
    /// Periodista a Sede 2, /health decia 25 huellas cargadas cuando Sede 2 tiene 2. Ese
    /// numero es el que super-admin muestra como "huellas cargadas" de cada lector.
    /// </summary>
    Task<int> CountAsync(string? tenantId = null);

    /// <summary>
    /// Borra del disco los templates de todo gimnasio que NO sea este. Se llama al vincular.
    ///
    /// `ReemplazarAsync` solo pisa la lista del tenant que recibe, asi que al mover un lector
    /// de un gimnasio a otro las huellas del anterior se quedaban en `templates.json` para
    /// siempre. Son datos biometricos (Ley 29733) de socios de un gimnasio que ya no es
    /// dueno de esa computadora. El comentario del endpoint de pairing decia desde el
    /// principio que "lo que este lector tuviera de antes no tiene por que sobrevivir": esto
    /// es lo que faltaba para que fuera cierto.
    /// </summary>
    Task OlvidarOtrosAsync(string tenantId);

    /// <summary>
    /// Contador que sube en cada SaveAsync. El device lo usa (junto al tenant) como clave
    /// de cache de su DB en memoria del SDK: mientras no cambie, no hace falta rearmarla.
    /// </summary>
    long Version { get; }
}

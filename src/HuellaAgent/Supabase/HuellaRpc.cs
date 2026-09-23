using System.Net.Http.Json;
using System.Text.Json;
using HuellaAgent.Config;
using HuellaAgent.Storage;

namespace HuellaAgent.Supabase;

/// <summary>Credenciales durables resueltas (pairing o appsettings).</summary>
public sealed record DurableCreds(string SupabaseUrl, string AnonKey, string Token);

/// <summary>
/// Persistencia DURABLE vía RPCs SECURITY DEFINER (anon key pública + kiosk_token). NUNCA
/// service-role. Las credenciales se resuelven dinámicamente: primero el pairing (cifrado,
/// onboarding SaaS), si no el appsettings (modo manual/dev). Así vincular por la app surte
/// efecto sin reiniciar el agente.
/// </summary>
public sealed class HuellaRpc
{
    private readonly AgentConfig _cfg;
    private readonly PairingStore _pairing;
    private readonly HttpClient _http = new();
    private readonly ILogger<HuellaRpc> _log;

    public HuellaRpc(AgentConfig cfg, PairingStore pairing, ILogger<HuellaRpc> log)
    {
        _cfg = cfg;
        _pairing = pairing;
        _log = log;
    }

    /// <summary>Credenciales activas: pairing tiene prioridad; si no, appsettings.</summary>
    public DurableCreds? Creds()
    {
        var p = _pairing.Load();
        if (p is not null && Has(p.Token, p.SupabaseUrl, p.AnonKey))
            return new DurableCreds(p.SupabaseUrl, p.AnonKey, p.Token);
        if (_cfg.SupabaseEnabled && Has(_cfg.KioskToken, _cfg.SupabaseUrl, _cfg.SupabaseAnonKey))
            return new DurableCreds(_cfg.SupabaseUrl, _cfg.SupabaseAnonKey, _cfg.KioskToken);
        return null;
    }

    public bool Enabled => Creds() is not null;

    private static bool Has(params string[] xs) => xs.All(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// Techo del pedido que decide si la puerta abre.
    ///
    /// `new HttpClient()` viene con 100 SEGUNDOS de timeout, y el portero espera el
    /// veredicto de forma secuencial: un pedido colgado —internet lento, no caido— le
    /// congela la puerta a TODOS los que vienen atras, hasta minuto y medio. Y el socio de
    /// adelante ya se fue.
    ///
    /// 5 s son doce veces lo medido contra produccion el 23-sep (400 ms de promedio,
    /// 545 ms el peor de cinco). Pasado eso se trata como "sin veredicto", que es el camino
    /// que ya existe: no abre y queda en el log. Preferimos que el socio pase por recepcion
    /// a que la puerta quede muerta un minuto y medio para todo el mundo.
    /// </summary>
    private static readonly TimeSpan TechoVeredicto = TimeSpan.FromSeconds(5);

    /// <summary>
    /// El enrolado y la carga de templates son otra cosa: nadie esta esperando en la
    /// puerta, hay alguien mirando la pantalla, y la lista de un gimnasio grande pesa.
    /// </summary>
    private static readonly TimeSpan TechoLargo = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Combina el techo propio con el `ct` de quien llama en vez de reemplazarlo: el de
    /// afuera corta cuando se apaga el agente, este corta el pedido colgado.
    /// </summary>
    private static CancellationTokenSource ConTecho(TimeSpan techo, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(techo);
        return cts;
    }

    private HttpRequestMessage Req(DurableCreds c, string fn, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            c.SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/" + fn) { Content = JsonContent.Create(body) };
        req.Headers.TryAddWithoutValidation("apikey", c.AnonKey);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + c.AnonKey);
        return req;
    }

    /// <summary>huella_enroll(token, cliente_id, template) → uid (server-side).</summary>
    public async Task<int?> EnrollAsync(string clienteId, string template, CancellationToken ct)
    {
        var c = Creds();
        if (c is null) return null;
        using var cts = ConTecho(TechoLargo, ct);
        var resp = await _http.SendAsync(Req(c, "huella_enroll",
            new { p_token = c.Token, p_cliente_id = clienteId, p_template = template }), cts.Token);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("huella_enroll fallo: {S}", resp.StatusCode); return null; }
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (json.TryGetProperty("ok", out var ok) && ok.GetBoolean() && json.TryGetProperty("uid", out var uid))
            return uid.GetInt32();
        return null;
    }

    /// <summary>Veredicto de `registrar_acceso`: el server ya decidió y ya registró.</summary>
    public sealed record Veredicto(bool Permitido, bool AbrirPuerta, string? Motivo, string? Tipo,
                                   string? Nombre, string? Mensaje, bool YaHoy);

    /// <summary>
    /// registrar_acceso(token, persona, metodo, score) → el JUEZ ÚNICO (migración
    /// 20260918124500). Decide vigencia, inicio, estado, cupo, "ya entró hoy" y staff;
    /// registra la asistencia o el rechazo; y contesta si hay que abrir la puerta.
    ///
    /// Es lo que permite que la puerta funcione con el navegador cerrado: hasta ahora
    /// quien decidía era la pestaña de Chrome.
    /// </summary>
    public async Task<Veredicto?> RegistrarAccesoAsync(string personaId, string metodo, int? score, CancellationToken ct)
    {
        var c = Creds();
        if (c is null) return null;
        try
        {
                using var cts = ConTecho(TechoVeredicto, ct);
            var resp = await _http.SendAsync(Req(c, "registrar_acceso",
                new { p_token = c.Token, p_persona_id = personaId, p_metodo = metodo, p_score = score }), cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("registrar_acceso fallo: {S}", resp.StatusCode);
                return null;
            }
            var j = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!j.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                _log.LogWarning("registrar_acceso rechazo el token: {Detalle}",
                    j.TryGetProperty("detail", out var d) ? d.GetString() : "(sin detalle)");
                return null;
            }
            return new Veredicto(
                Permitido: Bool(j, "permitido"),
                AbrirPuerta: Bool(j, "abrir_puerta"),
                Motivo: Str(j, "motivo"),
                Tipo: Str(j, "tipo"),
                Nombre: Str(j, "nombre"),
                Mensaje: Str(j, "mensaje"),
                YaHoy: Bool(j, "ya_hoy"));
        }
        catch (Exception ex)
        {
            // Sin internet o Supabase caído: el portero no puede decidir. Que lo resuelva
            // quien llama (hoy: no abrir; cuando exista la copia local, decidir con ella).
            _log.LogWarning(ex, "registrar_acceso no respondio");
            return null;
        }
    }

    private static bool Bool(JsonElement j, string campo) =>
        j.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement j, string campo) =>
        j.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>tenant_id del vínculo activo (el gym al que está vinculado el lector).</summary>
    public string? TenantId => _pairing.Load()?.TenantId;

    public sealed record TemplatesResult(string TenantId, IReadOnlyList<StoredTemplate> Templates);

    /// <summary>huella_templates(token) → {ok, tenant_id, templates} del tenant.</summary>
    public async Task<TemplatesResult?> TemplatesAsync(CancellationToken ct)
    {
        var c = Creds();
        if (c is null) return null;
        using var cts = ConTecho(TechoLargo, ct);
        var resp = await _http.SendAsync(Req(c, "huella_templates", new { p_token = c.Token }), cts.Token);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("huella_templates fallo: {S}", resp.StatusCode); return null; }
        var payload = await resp.Content.ReadFromJsonAsync<RpcTemplatesResponse>(cancellationToken: ct);
        if (payload is null || !payload.ok || string.IsNullOrEmpty(payload.tenant_id)) return null;
        var list = (payload.templates ?? new()).Select(r => new StoredTemplate
        {
            ClienteId = r.cliente_id ?? "",
            Uid = r.uid,
            Template = r.template ?? "",
        }).ToList();
        return new TemplatesResult(payload.tenant_id, list);
    }

    /// <summary>
    /// huella_config(token) -> la configuracion DEL GIMNASIO.
    ///
    /// null = no se pudo preguntar (sin internet, sin vinculo, o una base sin la migracion
    /// del 23-sep). Quien llama tiene que quedarse con lo que ya sabia: un corte de red no
    /// puede apagarle la puerta a un gimnasio.
    /// </summary>
    public async Task<ConfigGym?> ConfigDelGimnasioAsync(CancellationToken ct)
    {
        var c = Creds();
        if (c is null) return null;
        try
        {
            using var cts = ConTecho(TechoVeredicto, ct);
            var resp = await _http.SendAsync(Req(c, "huella_config", new { p_token = c.Token }), cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                // Una base sin la migracion contesta 404: no es un error que valga la pena
                // gritar en cada vuelta, el agente sigue con su appsettings.json.
                _log.LogDebug("huella_config no disponible: {S}", resp.StatusCode);
                return null;
            }
            var j = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            if (!j.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;

            return new ConfigGym(
                AbreSinNavegador: Bool(j, "abre_sin_navegador"),
                TieneTorniquete:  Bool(j, "tiene_torniquete"),
                PulsoMs:          Int(j, "pulso_ms", 700),
                Umbral:           Int(j, "umbral", 300));
        }
        catch (Exception ex)
        {
            _log.LogDebug("huella_config fallo: {M}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// lector_latido(token, maquina, datos): le cuenta al servidor como esta este lector.
    ///
    /// Es fire-and-forget: devuelve si salio bien solo para el log. Nada del agente puede
    /// depender de esto — el latido es para MIRAR, no para funcionar.
    /// </summary>
    public async Task<bool> LatidoAsync(string maquina, object datos, CancellationToken ct)
    {
        var c = Creds();
        if (c is null) return false;
        try
        {
            using var cts = ConTecho(TechoVeredicto, ct);
            var resp = await _http.SendAsync(
                Req(c, "lector_latido", new { p_token = c.Token, p_maquina = maquina, p_datos = datos }), cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static int Int(JsonElement j, string k, int porDefecto) =>
        j.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : porDefecto;

    /// <summary>Valida credenciales nuevas (pairing) con kiosk_init: confirma el token + devuelve gym.</summary>
    public async Task<(bool ok, string? tenantId, string? gym)> ValidateAsync(DurableCreds c, CancellationToken ct)
    {
        try
        {
            using var cts = ConTecho(TechoVeredicto, ct);
            var resp = await _http.SendAsync(Req(c, "kiosk_init", new { p_token = c.Token }), cts.Token);
            if (!resp.IsSuccessStatusCode) return (false, null, null);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!json.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return (false, null, null);
            string? tid = json.TryGetProperty("tenant_id", out var t) ? t.GetString() : null;
            string? gym = json.TryGetProperty("gym_nombre", out var g) ? g.GetString() : null;
            return (true, tid, gym);
        }
        catch { return (false, null, null); }
    }

    private sealed record RpcTemplatesResponse(bool ok, string? tenant_id, List<RpcTemplate>? templates);
    private sealed record RpcTemplate(string? cliente_id, int uid, string? template);
}

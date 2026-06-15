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
        var resp = await _http.SendAsync(Req(c, "huella_enroll",
            new { p_token = c.Token, p_cliente_id = clienteId, p_template = template }), ct);
        if (!resp.IsSuccessStatusCode) { _log.LogWarning("huella_enroll fallo: {S}", resp.StatusCode); return null; }
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (json.TryGetProperty("ok", out var ok) && ok.GetBoolean() && json.TryGetProperty("uid", out var uid))
            return uid.GetInt32();
        return null;
    }

    public sealed record TemplatesResult(string TenantId, IReadOnlyList<StoredTemplate> Templates);

    /// <summary>huella_templates(token) → {ok, tenant_id, templates} del tenant.</summary>
    public async Task<TemplatesResult?> TemplatesAsync(CancellationToken ct)
    {
        var c = Creds();
        if (c is null) return null;
        var resp = await _http.SendAsync(Req(c, "huella_templates", new { p_token = c.Token }), ct);
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

    /// <summary>Valida credenciales nuevas (pairing) con kiosk_init: confirma el token + devuelve gym.</summary>
    public async Task<(bool ok, string? tenantId, string? gym)> ValidateAsync(DurableCreds c, CancellationToken ct)
    {
        try
        {
            var resp = await _http.SendAsync(Req(c, "kiosk_init", new { p_token = c.Token }), ct);
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

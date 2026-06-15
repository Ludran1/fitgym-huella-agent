using System.Net.Http.Json;
using System.Text.Json;
using HuellaAgent.Config;
using HuellaAgent.Storage;

namespace HuellaAgent.Supabase;

/// <summary>
/// Persistencia DURABLE opcional via RPCs SECURITY DEFINER (anon key + kiosk_token).
/// OFF por defecto (reader-first usa solo el store local). Cuando se activen las RPCs
/// huella_enroll / huella_templates (necesitan OK de Angelpetus), este cliente las llama.
/// NUNCA usa service-role: solo la anon key publica + el token del tenant.
/// </summary>
public sealed class HuellaRpc
{
    private readonly AgentConfig _cfg;
    private readonly HttpClient _http;
    private readonly ILogger<HuellaRpc> _log;

    public HuellaRpc(AgentConfig cfg, ILogger<HuellaRpc> log)
    {
        _cfg = cfg;
        _log = log;
        _http = new HttpClient();
        if (!string.IsNullOrEmpty(cfg.SupabaseUrl))
            _http.BaseAddress = new Uri(cfg.SupabaseUrl.TrimEnd('/') + "/rest/v1/rpc/");
        if (!string.IsNullOrEmpty(cfg.SupabaseAnonKey))
        {
            _http.DefaultRequestHeaders.Add("apikey", cfg.SupabaseAnonKey);
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + cfg.SupabaseAnonKey);
        }
    }

    public bool Enabled => _cfg.SupabaseEnabled
        && !string.IsNullOrEmpty(_cfg.SupabaseUrl)
        && !string.IsNullOrEmpty(_cfg.SupabaseAnonKey)
        && !string.IsNullOrEmpty(_cfg.KioskToken);

    /// <summary>huella_enroll(token, cliente_id, template) → uid (server-side).</summary>
    public async Task<int?> EnrollAsync(string clienteId, string template, CancellationToken ct)
    {
        if (!Enabled) return null;
        var resp = await _http.PostAsJsonAsync("huella_enroll", new
        {
            p_token = _cfg.KioskToken,
            p_cliente_id = clienteId,
            p_template = template,
        }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("huella_enroll fallo: {Status}", resp.StatusCode);
            return null;
        }
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (json.TryGetProperty("ok", out var ok) && ok.GetBoolean()
            && json.TryGetProperty("uid", out var uid))
            return uid.GetInt32();
        return null;
    }

    /// <summary>Resultado de huella_templates: el tenant_id (para keyear el cache local) + sus templates.</summary>
    public sealed record TemplatesResult(string TenantId, IReadOnlyList<StoredTemplate> Templates);

    /// <summary>huella_templates(token) → {ok, tenant_id, templates} del tenant (carga inicial).</summary>
    public async Task<TemplatesResult?> TemplatesAsync(CancellationToken ct)
    {
        if (!Enabled) return null;
        var resp = await _http.PostAsJsonAsync("huella_templates", new { p_token = _cfg.KioskToken }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("huella_templates fallo: {Status}", resp.StatusCode);
            return null;
        }
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

    private sealed record RpcTemplatesResponse(bool ok, string? tenant_id, List<RpcTemplate>? templates);
    private sealed record RpcTemplate(string? cliente_id, int uid, string? template);
}

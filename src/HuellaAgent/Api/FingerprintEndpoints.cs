using HuellaAgent.Config;
using HuellaAgent.Devices;
using HuellaAgent.Relays;
using HuellaAgent.Storage;
using HuellaAgent.Supabase;

namespace HuellaAgent.Api;

// DTOs del contrato (huellaApi.ts). El binding snake_case ↔ PascalCase lo hace la
// JsonNamingPolicy.SnakeCaseLower configurada en Program.cs.
public sealed record CaptureReq(int? Timeout);
public sealed record EnrollReq(string ClienteId, string TenantId, string Template1, string Template2, string Template3);
public sealed record IdentifyReq(string TenantId);
public sealed record TurnstileReq(string TenantId);
public sealed record PairReq(string Token, string SupabaseUrl, string AnonKey);

/// <summary>
/// Los 4 endpoints que consume src/lib/huellaApi.ts (contrato LOCKEADO) + un endpoint
/// opcional de torniquete (Fase 7, NO parte del contrato del frontend).
/// </summary>
public static class FingerprintEndpoints
{
    public static void Map(WebApplication app)
    {
        // ── GET /health (sin api-key: el chip de estado lo pollea siempre) ──────────
        app.MapGet("/health", async (IFingerprintDevice device, ITemplateStore store, HuellaRpc rpc) =>
            TypedResults.Json(new
            {
                ok = true,
                reader = device.IsConnected ? "connected" : "disconnected",
                device = device.DeviceName,
                templates_loaded = await store.CountAsync(),
                durable = rpc.Enabled,   // true = vinculado a Supabase (pairing o appsettings)
            }));

        // ── /api/fingerprint/* (api-key opcional) ───────────────────────────────────
        var api = app.MapGroup("/api/fingerprint").AddEndpointFilter(ApiKeyFilter);

        // POST capture {timeout} → {template, quality} · 408 sin dedo
        api.MapPost("/capture", async (CaptureReq? body, IFingerprintDevice device, AgentConfig cfg, CancellationToken ct) =>
        {
            try
            {
                var r = await device.CaptureAsync(body?.Timeout ?? 15, ct);
                return Results.Json(new { template = r.Template, quality = r.Quality });
            }
            catch (NoFingerException) { return Results.Json(new { detail = "timeout sin dedo" }, statusCode: 408); }
            catch (DeviceUnavailableException ex) { return Results.Json(new { detail = ex.Message }, statusCode: 503); }
        });

        // POST enroll {cliente_id, tenant_id, template1..3} → {ok, uid}
        api.MapPost("/enroll", async (EnrollReq body, IFingerprintDevice device, ITemplateStore store, HuellaRpc rpc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.ClienteId) || string.IsNullOrWhiteSpace(body.TenantId)
                || string.IsNullOrWhiteSpace(body.Template1) || string.IsNullOrWhiteSpace(body.Template2) || string.IsNullOrWhiteSpace(body.Template3))
                return Results.Json(new { ok = false, detail = "faltan campos" }, statusCode: 400);

            string merged;
            try { merged = device.Merge(body.Template1, body.Template2, body.Template3); }
            catch (Exception ex) { return Results.Json(new { ok = false, detail = $"merge fallo: {ex.Message}" }, statusCode: 422); }

            var uid = await store.SaveAsync(body.TenantId, body.ClienteId, merged);
            if (rpc.Enabled) await rpc.EnrollAsync(body.ClienteId, merged, ct);   // mirror durable opcional
            return Results.Json(new { ok = true, uid });
        });

        // POST identify {tenant_id} → 200 {ok, cliente_id, score} · 404 sin match · 408 sin dedo
        api.MapPost("/identify", async (IdentifyReq body, IFingerprintDevice device, ITemplateStore store, AgentConfig cfg, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.TenantId))
                return Results.StatusCode(400);

            var db = await store.LoadAsync(body.TenantId);
            try
            {
                var match = await device.IdentifyAsync(db, cfg.IdentifyTimeoutSeconds, ct);
                if (match is null) return Results.StatusCode(404);              // dedo sin match
                var entry = db.FirstOrDefault(t => t.Uid == match.Uid);
                if (entry is null) return Results.StatusCode(404);
                return Results.Json(new { ok = true, cliente_id = entry.ClienteId, score = match.Score });
            }
            catch (NoFingerException) { return Results.StatusCode(408); }       // sin dedo
            catch (DeviceUnavailableException) { return Results.StatusCode(503); }
        });

        // ── POST /api/turnstile/open {tenant_id} (Fase 7, opcional) ─────────────────
        // NO es parte del contrato del frontend. El Kiosko lo llamaria DESPUES de que
        // kiosk_marcar_asistencia confirme una membresia valida (no abrir a morosos).
        app.MapPost("/api/turnstile/open", async (TurnstileReq body, IRelay relay, AgentConfig cfg, CancellationToken ct) =>
        {
            if (!cfg.TurnstileEnabled) return Results.Json(new { ok = false, detail = "torniquete deshabilitado" }, statusCode: 409);
            await relay.PulseAsync(cfg.RelayPulseMs, ct);
            return Results.Json(new { ok = true });
        }).AddEndpointFilter(ApiKeyFilter);

        // ── POST /api/pair {token, supabase_url, anon_key} (pairing 1-clic desde la app) ──
        // El admin (logueado, en la PC de recepción) manda el kiosk_token de su gym + la
        // url/anon-key públicas. El agente valida con kiosk_init, lo cifra (DPAPI) y prende
        // durable SIN reiniciar. Solo localhost; el token es secreto del propio gym.
        app.MapPost("/api/pair", async (PairReq body, HuellaRpc rpc, PairingStore pairing, ITemplateStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.SupabaseUrl) || string.IsNullOrWhiteSpace(body.AnonKey))
                return Results.Json(new { ok = false, detail = "faltan campos" }, statusCode: 400);

            var creds = new DurableCreds(body.SupabaseUrl, body.AnonKey, body.Token);
            var (ok, tenantId, gym) = await rpc.ValidateAsync(creds, ct);
            if (!ok || string.IsNullOrEmpty(tenantId))
                return Results.Json(new { ok = false, detail = "token invalido o gym suspendido" }, statusCode: 401);

            pairing.Save(new Pairing(body.Token, body.SupabaseUrl, body.AnonKey, tenantId));

            // Carga inicial de templates del tenant al cache local.
            var res = await rpc.TemplatesAsync(ct);
            if (res is not null)
                foreach (var r in res.Templates) await store.SaveAsync(res.TenantId, r.ClienteId, r.Template);

            return Results.Json(new { ok = true, tenant_id = tenantId, gym, templates = res?.Templates.Count ?? 0 });
        }).AddEndpointFilter(ApiKeyFilter);
    }

    /// <summary>Si AgentConfig.ApiKey esta seteada, exige el header x-api-key.</summary>
    private static async ValueTask<object?> ApiKeyFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var cfg = ctx.HttpContext.RequestServices.GetRequiredService<AgentConfig>();
        if (!string.IsNullOrEmpty(cfg.ApiKey))
        {
            var sent = ctx.HttpContext.Request.Headers["x-api-key"].ToString();
            if (sent != cfg.ApiKey) return Results.Json(new { detail = "api-key invalida" }, statusCode: 401);
        }
        return await next(ctx);
    }
}

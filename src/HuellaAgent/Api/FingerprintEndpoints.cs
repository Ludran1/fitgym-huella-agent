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
    private static readonly string Version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Identificador de ESTE arranque. El navegador descarta el primer match despues de que
    /// el agente se reinicia (dedo bufferado = asistencia fantasma), y hasta ahora lo
    /// aproximaba mirando si /health dejaba de responder: si el reinicio duraba menos que su
    /// ventana de deteccion, no se enteraba. Con esto lo sabe con certeza.
    /// </summary>
    private static readonly string BootId = Guid.NewGuid().ToString("N")[..12];

    public static void Map(WebApplication app)
    {
        // ── GET /health (sin api-key: el chip de estado lo pollea siempre) ──────────
        //
        // Tiene que poder responder, sin entrar a la PC, las preguntas que antes obligaban
        // a ir hasta el gym: ¿hay lector de verdad o simulado? ¿cual? ¿a que gym esta
        // vinculado? ¿leyo algo alguna vez? ¿que fue lo ultimo que fallo? ¿se reinicio?
        app.MapGet("/health", async (IFingerprintDevice device, ITemplateStore store, HuellaRpc rpc,
                                     FingerprintScanner scanner, PairingStore pairing, IRelay relay, AgentConfig cfg) =>
        {
            var vinculo = pairing.Load();
            // Reintenta abrir el rele (con su propio freno): si lo enchufaron con el agente
            // corriendo, el chip de la app se entera solo, sin tener que abrir la puerta.
            var releOk = cfg.TurnstileEnabled && relay.TryConnect();
            return TypedResults.Json(new
            {
                ok = true,
                reader = device.IsConnected ? "connected" : "disconnected",
                device = device.DeviceName,
                templates_loaded = await store.CountAsync(),
                durable = rpc.Enabled,   // true = vinculado a Supabase (pairing o appsettings)
                // A QUE gym esta vinculado. Sin esto, la tarjeta de Configuracion mostraba
                // "vinculado" en todos los tenants y el enroll moria con 409 sin explicacion.
                tenant_id = vinculo?.TenantId,
                gym = vinculo?.Gym,
                // Modo de lectura: "continuous" = el sensor lee siempre (v1.0.3, sin zona
                // muerta entre polls); "per_request" = camino viejo. Sirve para verificar
                // on-site, desde el navegador, que el fix esta activo en esa PC.
                scan = scanner.Enabled ? "continuous" : "per_request",
                // QUIÉN decide y abre. "agente" = el portero autónomo está activo y el panel
                // tiene que dejar de pedir identify (si no, se roban el dedo y la puerta
                // abre dos veces). "navegador" = como siempre.
                decide = cfg.AutoDecide && scanner.Enabled ? "agente" : "navegador",
                // Con que exigencia esta corriendo ESTE gym (escala 0-1000). Sin esto no
                // habia forma de saber, sin entrar a la PC, si el umbral quedo calibrado.
                threshold = cfg.IdentifyThreshold,
                // Cambia en cada arranque: le dice al navegador que el agente se reinicio,
                // sin tener que adivinarlo por un /health que dejo de responder.
                boot_id = BootId,
                connected_since = device.ConnectedSinceUtc,
                last_read_at = scanner.UltimaLecturaUtc,
                last_error = device.LastError,
                // "off" = este gym no tiene torniquete · "ready" = el rele responde ·
                // "error" = esta configurado pero no se puede abrir (con el motivo al lado).
                turnstile = !cfg.TurnstileEnabled ? "off" : releOk ? "ready" : "error",
                turnstile_error = cfg.TurnstileEnabled ? relay.LastError : null,
                version = Version,
            });
        });

        // ── GET /logs?lines=N (sin api-key: visor de errores en el navegador) ───────
        // El agente corre oculto → esto deja ver los ultimos logs sin buscar el archivo.
        // Lee el log mas reciente de %ProgramData%\HuellaAgent\logs (Serilog, shared).
        app.MapGet("/logs", (AgentConfig cfg, int? lines) =>
        {
            var dir = Path.Combine(Path.GetDirectoryName(cfg.StoragePath) ?? ".", "logs");
            if (!Directory.Exists(dir)) return Results.Text("(sin logs todavia)", "text/plain; charset=utf-8");
            var file = new DirectoryInfo(dir).GetFiles("agent-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null) return Results.Text("(sin logs todavia)", "text/plain; charset=utf-8");

            var n = Math.Clamp(lines ?? 200, 1, 2000);
            var all = ReadLinesShared(file.FullName);   // FileShare.ReadWrite: Serilog lo tiene abierto
            var tail = all.Length <= n ? all : all[^n..];
            return Results.Text($"# {file.Name} (ultimas {tail.Length} lineas)\n" + string.Join("\n", tail),
                "text/plain; charset=utf-8");
        });

        // ── /api/fingerprint/* (api-key opcional) ───────────────────────────────────
        var api = app.MapGroup("/api/fingerprint").AddEndpointFilter(ApiKeyFilter);

        // POST capture {timeout} → {template, mismo_apoyo} · 408 sin dedo
        api.MapPost("/capture", async (CaptureReq? body, IFingerprintDevice device, FingerprintScanner scanner, AgentConfig cfg, CancellationToken ct) =>
        {
            try
            {
                // Pausa el scanner continuo mientras dura el enrolado: si no, los dos
                // compiten por el mismo dedo (el loop se lo roba y /capture se cuelga
                // hasta el timeout) y ademas un dedo del enrolado terminaria marcando
                // asistencia por /identify.
                // Se pregunta ANTES de suspender: SuspendAsync marca "enrolando", asi que
                // preguntarlo despues daria true SIEMPRE, incluida la primera captura — y
                // entonces se le pediria levantar el dedo a alguien que recien lo apoyo.
                var dentroDeUnEnrolado = scanner.EnrolandoAhora;
                using var lectorTomado = await scanner.SuspendAsync(ct);

                // ¿Es una captura del MISMO apoyo que la anterior?
                //
                // Las tres capturas se fusionan (DBMerge). Si salen del mismo apoyo, sin que
                // el socio levante el dedo, el template queda angosto y esa persona lee mal
                // TODOS los dias — y eso ya no se arregla. El SDK no se queja: devuelve lo
                // que haya sobre el vidrio en el instante en que se le pregunta.
                //
                // Solo se exige el levante DENTRO de un enrolado en curso. Para la primera
                // captura da igual con que apoyo se arranca, y obligar a levantar ahi seria
                // pedirle algo raro a alguien que acaba de apoyar el dedo bien.
                var mismoApoyo = false;
                if (dentroDeUnEnrolado && !await EsperarQueSeLevanteAsync(device, cfg.EsperaLevanteMs, ct))
                    mismoApoyo = true;   // nunca lo levanto: se captura igual, pero avisado

                var r = await device.CaptureAsync(body?.Timeout ?? 15, ct);

                // Se captura igual y se deja decidir al que llama, en vez de tirar un error.
                // Fallar aca dejaria el enrolado trabado con un mensaje que el mostrador no
                // puede accionar; con el aviso, el frontend descarta y pide de nuevo.
                return Results.Json(new { template = r.Template, mismo_apoyo = mismoApoyo });
            }
            catch (NoFingerException) { return Results.Json(new { detail = "timeout sin dedo" }, statusCode: 408); }
            catch (DeviceUnavailableException ex) { return Results.Json(new { detail = ex.Message }, statusCode: 503); }
        });

        // POST enroll {cliente_id, tenant_id, template1..3} → {ok, uid}
        api.MapPost("/enroll", async (EnrollReq body, IFingerprintDevice device, ITemplateStore store, HuellaRpc rpc,
                                      AgentConfig cfg, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.ClienteId) || string.IsNullOrWhiteSpace(body.TenantId)
                || string.IsNullOrWhiteSpace(body.Template1) || string.IsNullOrWhiteSpace(body.Template2) || string.IsNullOrWhiteSpace(body.Template3))
                return Results.Json(new { ok = false, detail = "faltan campos" }, statusCode: 400);

            // Calidad del enrolado ANTES de guardar. Las 3 capturas tienen que parecerse
            // entre sí: si el socio movió el dedo, lo apoyó de canto o usó dedos distintos,
            // el template que se guarda queda flojo y esa persona va a leer mal TODOS los
            // días — y eso ya no se arregla después. Es más barato pedir que repita ahora.
            var pares = new[]
            {
                device.Match(body.Template1, body.Template2),
                device.Match(body.Template1, body.Template3),
                device.Match(body.Template2, body.Template3),
            };
            var peor = pares.Min();
            var logEnroll = lf.CreateLogger("Enroll");
            logEnroll.LogInformation("enroll: capturas {A}/{B}/{C} (minimo exigido {Min})",
                pares[0], pares[1], pares[2], cfg.EnrollMinScore);

            if (peor < cfg.EnrollMinScore)
                return Results.Json(new
                {
                    ok = false,
                    detail = "Las 3 capturas no coinciden entre sí. Apoyá el MISMO dedo, bien centrado y sin moverlo, y volvé a intentar.",
                    calidad = peor,
                }, statusCode: 422);

            string merged;
            try { merged = device.Merge(body.Template1, body.Template2, body.Template3); }
            catch (Exception ex) { return Results.Json(new { ok = false, detail = $"merge fallo: {ex.Message}" }, statusCode: 422); }

            var uid = await store.SaveAsync(body.TenantId, body.ClienteId, merged);

            // Persistencia durable. ANTES esto era "best-effort" e ignoraba el resultado:
            // si el agente estaba vinculado a OTRO gym, huella_enroll devolvia ok:false y
            // el agente IGUAL respondia ok:true → la huella quedaba SOLO en el dispositivo
            // (se pierde al reiniciar, no sincroniza, no anda en otra PC). El frontend
            // mostraba "exito" pero la tabla huellas seguia vacia. Ahora se reporta el hecho.
            // La calidad viaja tambien cuando SALE BIEN, no solo en el rechazo.
            //
            // El agente ya la calculaba y la dejaba en el log; el frontend solo la veia si
            // el enrolado fallaba. Y el numero tiene DOS lados malos: por abajo son dedos
            // distintos (eso ya se rechaza con EnrollMinScore), y por arriba, saturado, son
            // las tres capturas del mismo apoyo — que pasan sin quejarse y dan una huella
            // que falla todos los dias. Sin el numero a la vista, ese caso es invisible.
            if (!rpc.Enabled)
                // Sin vincular: guardado local-only. durable=false → el frontend avisa
                // (en un gym la huella DEBE persistir en la nube; no es exito real).
                return Results.Json(new { ok = true, uid, durable = false, calidad = peor, pares });

            var durableUid = await rpc.EnrollAsync(body.ClienteId, merged, ct);
            if (durableUid is null)
                return Results.Json(new { ok = false, detail = "Guardado en el lector pero NO en la nube: el lector esta vinculado a otro gym o el cliente no pertenece a este gym. Re-vincula el lector (Configuracion -> Lector de huella) y volve a enrolar." }, statusCode: 409);

            return Results.Json(new { ok = true, uid, durable = true, calidad = peor, pares });
        });

        // POST identify {tenant_id} → 200 {ok, cliente_id, score} · 404 sin match · 408 sin dedo
        //
        // v1.0.3: ya NO arma el sensor por request. El FingerprintScanner lo mantiene
        // leyendo siempre; aca solo se espera (long-poll) a que aparezca un dedo en su
        // buffer y se hace el 1:N. Un dedo apoyado mientras el frontend dormia entre polls
        // ya esta bufferado y vuelve al instante: se acabo la zona muerta de ~25% que
        // causaba el "a veces agarra, a veces no".
        api.MapPost("/identify", async (IdentifyReq body, IFingerprintDevice device, FingerprintScanner scanner,
                                        ITemplateStore store, AgentConfig cfg, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.TenantId))
                return Results.StatusCode(400);

            var log = lf.CreateLogger("Identify");
            var db = await store.LoadAsync(body.TenantId);
            try
            {
                if (!scanner.Enabled)
                {
                    // Camino legacy (Agent:ContinuousScan=false): valvula de escape si el
                    // scanner se porta mal contra hardware real, sin necesidad de rollback.
                    var legacy = await device.IdentifyAsync(db, cfg.IdentifyTimeoutSeconds, ct);
                    Anotar(log, legacy, db, cfg);
                    return Match(legacy, db);
                }

                var probe = await scanner.WaitForProbeAsync(cfg.IdentifyTimeoutSeconds, ct);
                if (probe is null) return Results.StatusCode(408);            // sin dedo

                // La clave incluye el tenant Y la version del store: la DB en memoria del
                // SDK se reusa entre polls y se rearma sola al enrolar o cambiar de tenant.
                var match = device.Identify(probe.Template, db, $"{body.TenantId}:{store.Version}");
                Anotar(log, match, db, cfg);
                return Match(match, db);
            }
            catch (NoFingerException) { return Results.StatusCode(408); }       // sin dedo
            catch (DeviceUnavailableException) { return Results.StatusCode(503); }
            catch (OperationCanceledException) { return Results.StatusCode(408); } // el cliente corto
        });


        // ── POST /api/turnstile/open {tenant_id} (Fase 7, opcional) ─────────────────
        // NO es parte del contrato del frontend. El Kiosko lo llamaria DESPUES de que
        // kiosk_marcar_asistencia confirme una membresia valida (no abrir a morosos).
        app.MapPost("/api/turnstile/open", async (TurnstileReq body, IRelay relay, AgentConfig cfg,
                                                  ILoggerFactory lf, CancellationToken ct) =>
        {
            if (!cfg.TurnstileEnabled) return Results.Json(new { ok = false, detail = "torniquete deshabilitado" }, statusCode: 409);
            try
            {
                await relay.PulseAsync(cfg.RelayPulseMs, ct);
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                // El rele desenchufado o el COM ocupado tiraban una excepcion sin manejar:
                // 500 sin explicacion, y el frontend (que solo se apaga ante un 409) seguia
                // insistiendo en cada entrada. Ahora es un 503 con el motivo, igual que el
                // lector, y queda en el log.
                lf.CreateLogger("Turnstile").LogError(ex, "No se pudo pulsar el rele ({Port})", cfg.RelayPort);
                return Results.Json(new { ok = false, detail = $"el rele no respondio: {ex.Message}" }, statusCode: 503);
            }
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

            // El nombre del gym se guarda para que /health pueda decir a CUAL esta vinculado.
            pairing.Save(new Pairing(body.Token, body.SupabaseUrl, body.AnonKey, tenantId, gym));

            // Carga inicial de templates del tenant al cache local.
            // Reemplaza en vez de fusionar: al vincular a un gimnasio, lo que este lector
            // tuviera de antes (otra PC, otro gym) no tiene por que sobrevivir.
            var res = await rpc.TemplatesAsync(ct);
            if (res is not null) await store.ReemplazarAsync(res.TenantId, res.Templates);

            return Results.Json(new { ok = true, tenant_id = tenantId, gym, templates = res?.Templates.Count ?? 0 });
        }).AddEndpointFilter(ApiKeyFilter);
    }

    /// <summary>
    /// Deja el puntaje de CADA lectura en el log del agente.
    ///
    /// Hasta el 18-sep el puntaje solo quedaba cuando el navegador llegaba a marcar la
    /// asistencia, asi que el umbral se iba a calibrar a ciegas. Con esto, una semana de
    /// uso normal deja cientos de lecturas de socios reales en
    /// %ProgramData%\HuellaAgent\logs, que es el insumo para decidir el umbral de verdad.
    /// Se anota el uid interno del lector, no el uuid de la persona: alcanza para la
    /// estadistica y el log queda con menos datos personales.
    /// </summary>
    private static void Anotar(ILogger log, IdentifyMatch? match, IReadOnlyList<StoredTemplate> db, AgentConfig cfg)
    {
        if (match is null)
        {
            log.LogInformation("identify: dedo leido SIN coincidencia (umbral {Umbral}, {Cuantas} huellas cargadas)",
                cfg.IdentifyThreshold, db.Count);
            return;
        }
        log.LogInformation("identify: match uid={Uid} score={Score} (umbral {Umbral}, {Cuantas} huellas cargadas)",
            match.Uid, match.Score, cfg.IdentifyThreshold, db.Count);
    }

    /// <summary>
    /// Espera a que el vidrio quede libre. true = se levanto; false = se acabo la paciencia.
    ///
    /// `TryCapture()` devuelve null cuando no hay dedo, asi que el agente SABE si esta
    /// apoyado — no hace falta adivinarlo por tiempos, que es lo unico que puede hacer el
    /// navegador (hoy descarta una captura que vuelve en menos de 500 ms).
    /// </summary>
    private static async Task<bool> EsperarQueSeLevanteAsync(IFingerprintDevice device, int msMax, CancellationToken ct)
    {
        var hasta = DateTime.UtcNow.AddMilliseconds(Math.Max(0, msMax));
        while (DateTime.UtcNow < hasta)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (device.TryCapture() is null) return true;
            }
            catch (DeviceUnavailableException)
            {
                return true;   // el lector se cayo: que lo reporte CaptureAsync, no esto
            }
            await Task.Delay(40, ct);
        }
        return false;
    }

    /// <summary>Traduce el match del SDK al contrato HTTP: 200 con cliente_id, o 404.</summary>
    private static IResult Match(IdentifyMatch? match, IReadOnlyList<StoredTemplate> db)
    {
        if (match is null) return Results.StatusCode(404);               // dedo sin match
        var entry = db.FirstOrDefault(t => t.Uid == match.Uid);
        if (entry is null) return Results.StatusCode(404);               // uid huerfano
        return Results.Json(new { ok = true, cliente_id = entry.ClienteId, score = match.Score });
    }

    /// <summary>Lee todas las lineas de un archivo que otro proceso (Serilog) tiene abierto.</summary>
    private static string[] ReadLinesShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) is not null) lines.Add(line);
            return lines.ToArray();
        }
        catch (Exception ex) { return new[] { $"(no se pudo leer el log: {ex.Message})" }; }
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

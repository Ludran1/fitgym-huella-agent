using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Fija la semantica HTTP que consume src/lib/huellaApi.ts. Si algo aca se rompe, el
/// frontend deja de hablar con el agente. Todo corre con FakeDevice (sin hardware).
/// </summary>
public class ContractTests
{
    private static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res)
        => JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Health_reports_connected_and_template_count()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();

        var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("connected", body.GetProperty("reader").GetString());
        Assert.Equal(0, body.GetProperty("templates_loaded").GetInt32());
    }

    [Fact]
    public async Task Capture_returns_template_and_quality()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();

        var res = await client.PostAsync("/api/fingerprint/capture", Json("{\"timeout\":5}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("CAP-AAA", body.GetProperty("template").GetString());
        Assert.Equal(90, body.GetProperty("quality").GetInt32());
    }

    [Fact]
    public async Task Capture_without_finger_returns_408()
    {
        using var f = new AgentFactory();
        f.Device.CaptureNoFinger = true;
        var client = f.CreateClient();

        var res = await client.PostAsync("/api/fingerprint/capture", Json("{\"timeout\":1}"));
        Assert.Equal(HttpStatusCode.RequestTimeout, res.StatusCode);   // 408
    }

    [Fact]
    public async Task Enroll_assigns_uids_reuses_on_reenroll_and_isolates_tenants()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();

        // Primer cliente del tenant A → uid 1
        var u1 = await Enroll(client, "cliA1", "tenantA");
        Assert.Equal(1, u1);

        // Segundo cliente del tenant A → uid 2
        var u2 = await Enroll(client, "cliA2", "tenantA");
        Assert.Equal(2, u2);

        // Re-enroll del primer cliente → reusa uid 1
        var u1b = await Enroll(client, "cliA1", "tenantA");
        Assert.Equal(1, u1b);

        // Cliente de OTRO tenant arranca su propio uid en 1 (aislamiento)
        var uB = await Enroll(client, "cliB1", "tenantB");
        Assert.Equal(1, uB);
    }

    [Fact]
    public async Task Enroll_without_pairing_reports_durable_false()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();

        // Sin pairing (rpc deshabilitado) el enroll guarda local pero NO en la nube.
        // Debe responder durable=false para que el frontend avise (no es exito real).
        var raw = "{\"cliente_id\":\"cliA1\",\"tenant_id\":\"tenantA\"," +
                  "\"template1\":\"t1\",\"template2\":\"t2\",\"template3\":\"t3\"}";
        var res = await client.PostAsync("/api/fingerprint/enroll", Json(raw));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.False(body.GetProperty("durable").GetBoolean());
    }

    [Fact]
    public async Task Identify_match_returns_cliente_id()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();

        var uid = await Enroll(client, "cliente-xyz", "tenantA");
        f.Device.IdentifyMatchUid = uid;

        var res = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantA\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("cliente-xyz", body.GetProperty("cliente_id").GetString());
        Assert.Equal(99, body.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Identify_finger_without_match_returns_404()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();
        await Enroll(client, "cliente-xyz", "tenantA");

        f.Device.IdentifyNoMatch = true;
        var res = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantA\"}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);   // 404
    }

    [Fact]
    public async Task Identify_without_finger_returns_408()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();
        await Enroll(client, "cliente-xyz", "tenantA");

        f.Device.IdentifyNoFinger = true;
        var res = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantA\"}"));
        Assert.Equal(HttpStatusCode.RequestTimeout, res.StatusCode);   // 408
    }

    [Fact]
    public async Task Identify_other_tenant_has_no_templates_returns_404()
    {
        using var f = new AgentFactory();
        var client = f.CreateClient();
        await Enroll(client, "cliente-xyz", "tenantA");

        // tenantB no tiene templates → identify cae a "sin match" aunque haya dedo
        var res = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantB\"}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ApiKey_enforced_when_configured()
    {
        using var f = new AgentFactory(apiKey: "secreta-123");
        var client = f.CreateClient();

        // Sin header → 401
        var noKey = await client.PostAsync("/api/fingerprint/capture", Json("{\"timeout\":1}"));
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        // Con header correcto → 200
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/fingerprint/capture") { Content = Json("{\"timeout\":1}") };
        req.Headers.Add("x-api-key", "secreta-123");
        var withKey = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);

        // /health NO exige api-key (el chip lo pollea siempre)
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static async Task<int> Enroll(HttpClient client, string clienteId, string tenantId)
    {
        var raw = $"{{\"cliente_id\":\"{clienteId}\",\"tenant_id\":\"{tenantId}\"," +
                  "\"template1\":\"t1\",\"template2\":\"t2\",\"template3\":\"t3\"}";
        var res = await client.PostAsync("/api/fingerprint/enroll", Json(raw));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.True(body.GetProperty("ok").GetBoolean());
        return body.GetProperty("uid").GetInt32();
    }
}

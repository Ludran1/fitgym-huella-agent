using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Regresion del bug "a veces agarra, a veces no", al nivel del contrato HTTP que consume
/// el frontend.
///
/// El frontend NO pollea /identify sin parar: entre respuesta y respuesta duerme (POLL_MS).
/// Con el modelo viejo el agente cerraba el sensor al responder, asi que ese sleep era una
/// ZONA MUERTA: el socio que apoyaba el dedo justo ahi no era leido y tenia que reintentar.
/// En el kiosko eran ~1.1-1.5s ciegos de cada ~5.2s (~25% del tiempo).
///
/// Los dos tests de abajo simulan exactamente eso -un dedo apoyado MIENTRAS nadie esta
/// pidiendo /identify- y contrastan los dos modelos:
///   - ContinuousScan=false (viejo): la lectura se PIERDE  -> 408
///   - ContinuousScan=true  (nuevo): la lectura se BUFFEREA -> 200 con el cliente
/// </summary>
public class DeadZoneTests
{
    private static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

    private static async Task Enroll(HttpClient client, string clienteId, string tenantId)
    {
        var raw = $"{{\"cliente_id\":\"{clienteId}\",\"tenant_id\":\"{tenantId}\"," +
                  "\"template1\":\"t1\",\"template2\":\"t2\",\"template3\":\"t3\"}";
        var res = await client.PostAsync("/api/fingerprint/enroll", Json(raw));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    /// <summary>
    /// Apoya y levanta el dedo mientras NADIE esta esperando en /identify.
    ///
    /// 600ms a proposito: tras publicar un dedo el scanner se toma SameFingerDebounceMs
    /// (400ms) sin muestrear, para no republicar el mismo dedo apoyado. Una pulsacion mas
    /// corta que ese debounce puede caer entera adentro y no verse -irreal de todos modos:
    /// una persona apoya el dedo bastante mas que eso-. Con 600 > 400 siempre queda al
    /// menos una ventana de muestreo, sea cual sea la fase del debounce.
    /// </summary>
    private static async Task PressFingerWhileNobodyIsPolling(FakeDevice device)
    {
        device.IdentifyNoFinger = false;   // apoya
        await Task.Delay(600);             // el scanner (poll 40ms) tiene tiempo de verlo
        device.IdentifyNoFinger = true;    // levanta
    }

    /// <summary>
    /// EL test del fix. El dedo se apoya durante el sleep del frontend; el siguiente
    /// /identify debe devolverlo igual, desde el buffer, y al instante.
    /// </summary>
    [Fact]
    public async Task Finger_pressed_between_polls_is_still_identified()
    {
        using var f = new AgentFactory(continuousScan: true);
        var client = f.CreateClient();
        f.Device.IdentifyNoFinger = true;              // arranca sin dedo

        await Enroll(client, "cli-1", "tenantA");
        await PressFingerWhileNobodyIsPolling(f.Device);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantA\"}"));
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("cli-1", body.GetProperty("cliente_id").GetString());
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"debio salir del buffer al instante, tardo {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// El mismo escenario contra el modelo VIEJO, para dejar el bug documentado: la misma
    /// lectura se pierde y el socio se queda afuera. Si este test empieza a devolver 200,
    /// es que alguien dejo el scanner prendido cuando deberia estar apagado.
    /// </summary>
    [Fact]
    public async Task Legacy_per_request_model_loses_the_finger_pressed_between_polls()
    {
        using var f = new AgentFactory(continuousScan: false);
        var client = f.CreateClient();
        f.Device.IdentifyNoFinger = true;

        await Enroll(client, "cli-1", "tenantA");
        await PressFingerWhileNobodyIsPolling(f.Device);

        // Sin sensor armado durante la pausa, ese dedo no existio para el agente.
        var res = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantA\"}"));
        Assert.Equal(HttpStatusCode.RequestTimeout, res.StatusCode);   // 408: se perdio
    }

    /// <summary>
    /// La DB en memoria del SDK se reusa entre polls y solo se rearma al enrolar. Antes se
    /// hacia DBInit + N*DBAdd + DBFree en CADA poll (costo lineal con la cantidad de socios,
    /// cada pocos segundos, para tirarla a la basura).
    /// </summary>
    [Fact]
    public async Task Sdk_db_is_rebuilt_only_when_templates_change()
    {
        using var f = new AgentFactory(continuousScan: true);
        var client = f.CreateClient();
        f.Device.IdentifyNoFinger = true;

        await Enroll(client, "cli-1", "tenantA");

        // Tres identifies seguidos, sin enrolar nada en el medio.
        for (var i = 0; i < 3; i++)
        {
            await PressFingerWhileNobodyIsPolling(f.Device);
            var res = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantA\"}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        Assert.Equal(1, f.Device.DbRebuilds);   // una sola vez, no una por poll

        // Un enroll nuevo invalida el cache -> el proximo identify rearma.
        await Enroll(client, "cli-2", "tenantA");
        await PressFingerWhileNobodyIsPolling(f.Device);
        var res2 = await client.PostAsync("/api/fingerprint/identify", Json("{\"tenant_id\":\"tenantA\"}"));
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.Equal(2, f.Device.DbRebuilds);
    }
}

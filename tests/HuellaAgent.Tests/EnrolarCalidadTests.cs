using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Una huella mal enrolada no se arregla despues: esa persona lee mal todos los dias.
/// El enrolado exige que las 3 capturas se parezcan entre si antes de guardar nada.
/// </summary>
public class EnrolarCalidadTests
{
    private static object Cuerpo() => new
    {
        cliente_id = "11111111-1111-1111-1111-111111111111",
        tenant_id = "22222222-2222-2222-2222-222222222222",
        template1 = "CAP-A", template2 = "CAP-B", template3 = "CAP-C",
    };

    [Fact]
    public async Task Tres_capturas_que_no_se_parecen_se_rechazan_y_no_guardan_nada()
    {
        await using var app = new AgentFactory();
        app.Device.MatchScore = 120;   // el socio movio el dedo entre capturas
        var http = app.CreateClient();

        var r = await http.PostAsJsonAsync("/api/fingerprint/enroll", Cuerpo());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("MISMO dedo", await r.Content.ReadAsStringAsync());

        // Y el lector queda como estaba: no se guardo una huella floja.
        var health = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("/health");
        Assert.Equal(0, health.GetProperty("templates_loaded").GetInt32());
    }

    [Fact]
    public async Task Tres_capturas_consistentes_se_guardan()
    {
        await using var app = new AgentFactory();
        app.Device.MatchScore = 700;
        var http = app.CreateClient();

        var r = await http.PostAsJsonAsync("/api/fingerprint/enroll", Cuerpo());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var health = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("/health");
        Assert.Equal(1, health.GetProperty("templates_loaded").GetInt32());
    }

    /// <summary>
    /// El puntaje viaja tambien cuando el enrolado SALE BIEN.
    ///
    /// El agente ya lo calculaba y lo dejaba en el log; el frontend solo lo veia si fallaba.
    /// Y el numero tiene dos lados malos: por abajo son dedos distintos —eso ya se rechaza—
    /// y por arriba, saturado, son las tres capturas del MISMO apoyo, que pasan sin quejarse
    /// y dan una huella que falla todos los dias. Sin el numero a la vista es invisible.
    /// </summary>
    [Fact]
    public async Task El_enrolado_que_sale_bien_igual_dice_con_que_calidad()
    {
        await using var app = new AgentFactory();
        app.Device.MatchScore = 780;
        var http = app.CreateClient();

        var r = await http.PostAsJsonAsync("/api/fingerprint/enroll", Cuerpo());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(780, body.GetProperty("calidad").GetInt32());
        // Los tres 1:1 por separado: dos altos y uno bajo no es lo mismo que tres medios,
        // y el promedio lo taparia.
        Assert.Equal(3, body.GetProperty("pares").GetArrayLength());
    }
}

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HuellaAgent.Devices;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// El puntaje de cada captura, en el momento.
///
/// Antes la comparacion entre las 3 capturas pasaba SOLO en /enroll: el mostrador apoyaba
/// el dedo tres veces y recien ahi se enteraba de que no coincidian, con todo para rehacer
/// desde la primera. Ahora /capture devuelve cuanto se parece la nueva lectura a las que
/// ya hay, asi la segunda que no pega se detecta en la segunda.
///
/// Lo que NO se puede: puntuar la primera. El puntaje es un `Match` entre dos capturas, y
/// en la primera no hay contra que compararla. Para esa esta la imagen.
/// </summary>
public class PuntajeEnCadaCapturaTests
{
    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    /// El serializador OMITE las propiedades nulas, asi que "sin puntajes" puede llegar
    /// como clave ausente o como null. El panel tiene que tolerar las dos — y un agente
    /// anterior a la v1.3 directamente no manda ninguna de estas claves.
    private static bool SinDato(JsonElement cuerpo, string clave) =>
        !cuerpo.TryGetProperty(clave, out var v) || v.ValueKind == JsonValueKind.Null;

    private static async Task<JsonElement> Capturar(HttpClient http, string cuerpo)
    {
        var res = await http.PostAsync("/api/fingerprint/capture", Json(cuerpo));
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task La_primera_captura_no_trae_puntaje_porque_no_hay_con_que_comparar()
    {
        await using var app = new AgentFactory();
        var cuerpo = await Capturar(app.CreateClient(), "{\"timeout\":5}");

        Assert.True(SinDato(cuerpo, "puntajes"));
        Assert.False(string.IsNullOrEmpty(cuerpo.GetProperty("template").GetString()));
    }

    [Fact]
    public async Task Desde_la_segunda_se_compara_contra_las_anteriores()
    {
        await using var app = new AgentFactory();
        app.Device.MatchScore = 870;
        var cuerpo = await Capturar(app.CreateClient(), "{\"timeout\":5,\"previos\":[\"CAP-A\",\"CAP-B\"]}");

        var puntajes = cuerpo.GetProperty("puntajes").EnumerateArray().Select(p => p.GetInt32()).ToArray();
        Assert.Equal(new[] { 870, 870 }, puntajes);
    }

    [Fact]
    public async Task Viaja_el_minimo_exigido_para_que_el_panel_no_lo_tenga_que_adivinar()
    {
        // El umbral vive en la config del agente (`EnrollMinScore`) y lo aplica /enroll.
        // Si el panel lo tuviera escrito a mano, cambiarlo en una PC haria que el panel
        // dijera "buena" sobre una captura que el enrolado despues rechaza.
        await using var app = new AgentFactory();
        var cuerpo = await Capturar(app.CreateClient(), "{\"timeout\":5}");

        Assert.Equal(300, cuerpo.GetProperty("minimo").GetInt32());
    }

    [Fact]
    public async Task Un_template_previo_roto_no_tumba_la_captura()
    {
        // Si comparar falla, la lectura vale igual y se devuelve sin puntajes: el que
        // decide de verdad sigue siendo /enroll. Tirar un error aca dejaria el enrolado
        // trabado por un dato auxiliar.
        await using var app = new AgentFactory();
        app.Device.MatchThrows = true;
        var cuerpo = await Capturar(app.CreateClient(), "{\"timeout\":5,\"previos\":[\"ROTO\"]}");

        Assert.False(string.IsNullOrEmpty(cuerpo.GetProperty("template").GetString()));
        Assert.True(SinDato(cuerpo, "puntajes"));
    }

    [Fact]
    public async Task La_imagen_del_dedo_viaja_en_cada_captura()
    {
        await using var app = new AgentFactory();
        app.Device.Capture = new CaptureResult("CAP-AAA", "Qk0AAAA=");
        var cuerpo = await Capturar(app.CreateClient(), "{\"timeout\":5}");

        Assert.Equal("Qk0AAAA=", cuerpo.GetProperty("imagen").GetString());
    }
}

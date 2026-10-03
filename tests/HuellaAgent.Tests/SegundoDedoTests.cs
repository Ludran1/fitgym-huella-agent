using HuellaAgent.Storage;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Dos huellas por persona.
///
/// ZKTeco recomienda enrolar un dedo de CADA MANO. Hasta la v1.3 guardabamos una sola, y
/// eso es lo que mas friccion genera en la puerta: un socio con un corte, una curita o la
/// mano seca en invierno no entra y NO TIENE ALTERNATIVA.
///
/// Lo que estos tests fijan es el modo de falla silencioso: `SaveAsync` buscaba por
/// ClienteId a secas, asi que enrolar el segundo dedo PISABA el primero. El socio apoyaba
/// el dedo tres veces mas y terminaba con una sola huella — exactamente lo contrario de lo
/// que se le acababa de pedir, y sin un solo mensaje de error.
/// </summary>
public class SegundoDedoTests : IDisposable
{
    private const string Gym = "11111111-1111-1111-1111-111111111111";
    private const string Socio = "22222222-2222-2222-2222-222222222222";

    private readonly string _archivo = Path.Combine(Path.GetTempPath(), $"dedos-{Guid.NewGuid():N}.json");
    private LocalFileStore Store() => new(_archivo);

    public void Dispose() { try { File.Delete(_archivo); } catch { } }

    [Fact]
    public async Task El_segundo_dedo_NO_pisa_al_primero()
    {
        var store = Store();
        await store.SaveAsync(Gym, Socio, "HUELLA-INDICE-DER", dedo: 1);
        await store.SaveAsync(Gym, Socio, "HUELLA-INDICE-IZQ", dedo: 2);

        var todas = await store.LoadAsync(Gym);
        Assert.Equal(2, todas.Count);
        Assert.Contains(todas, t => t.Template == "HUELLA-INDICE-DER" && t.Dedo == 1);
        Assert.Contains(todas, t => t.Template == "HUELLA-INDICE-IZQ" && t.Dedo == 2);
    }

    [Fact]
    public async Task Cada_dedo_tiene_su_uid_porque_el_1N_resuelve_por_uid()
    {
        var store = Store();
        var uid1 = await store.SaveAsync(Gym, Socio, "A", dedo: 1);
        var uid2 = await store.SaveAsync(Gym, Socio, "B", dedo: 2);

        // Si compartieran uid, `identify` no podria distinguir cual template matcheo — y
        // peor, la DB en memoria del SDK se arma por uid: el segundo pisaria al primero.
        Assert.NotEqual(uid1, uid2);
    }

    [Fact]
    public async Task Re_enrolar_el_MISMO_dedo_reemplaza_y_conserva_su_uid()
    {
        var store = Store();
        var uid = await store.SaveAsync(Gym, Socio, "VIEJA", dedo: 1);
        await store.SaveAsync(Gym, Socio, "OTRO-DEDO", dedo: 2);
        var uidOtraVez = await store.SaveAsync(Gym, Socio, "NUEVA", dedo: 1);

        Assert.Equal(uid, uidOtraVez);   // uid estable → la DB del SDK no se invalida de mas
        var todas = await store.LoadAsync(Gym);
        Assert.Equal(2, todas.Count);
        Assert.Contains(todas, t => t.Dedo == 1 && t.Template == "NUEVA");
        Assert.DoesNotContain(todas, t => t.Template == "VIEJA");
    }

    [Fact]
    public async Task Sin_indicar_dedo_se_comporta_como_siempre()
    {
        // Un panel anterior a esto no manda `dedo`. Tiene que seguir enrolando el principal
        // y re-enrolando sobre el mismo, no creando uno nuevo cada vez.
        var store = Store();
        await store.SaveAsync(Gym, Socio, "PRIMERA");
        await store.SaveAsync(Gym, Socio, "SEGUNDA");

        var todas = await store.LoadAsync(Gym);
        Assert.Single(todas);
        Assert.Equal("SEGUNDA", todas[0].Template);
        Assert.Equal(1, todas[0].Dedo);
    }

    [Fact]
    public async Task Una_huella_guardada_por_un_agente_viejo_se_lee_como_dedo_1()
    {
        // `templates.json` de un agente anterior a la v1.4 no trae el campo. Si se leyera
        // como 0, el proximo enrolado del principal crearia una fila nueva en vez de pisar
        // la vieja, y esa persona quedaria con dos huellas del mismo dedo.
        await File.WriteAllTextAsync(_archivo,
            $$"""{"{{Gym}}":[{"ClienteId":"{{Socio}}","Uid":7,"Template":"VIEJA-SIN-DEDO"}]}""");

        var store = Store();
        var todas = await store.LoadAsync(Gym);

        Assert.Single(todas);
        Assert.Equal(1, todas[0].Dedo);

        var uid = await store.SaveAsync(Gym, Socio, "ACTUALIZADA");
        Assert.Equal(7, uid);
        Assert.Single(await store.LoadAsync(Gym));
    }
}

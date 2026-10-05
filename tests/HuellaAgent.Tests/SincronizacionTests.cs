using HuellaAgent.Storage;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Lo que pasa cuando el lector se mueve entre PCs.
///
/// El caso real: se desenchufa de la PC A, se enrola varios días en la PC B, y se vuelve a
/// la A. Las dos computadoras nunca se hablan entre sí — Supabase es el único punto de
/// encuentro— y el reconocimiento es 100% local, contra `templates.json`.
///
/// Las huellas NUEVAS llegaban bien: la carga inicial las baja al arrancar el agente. Lo
/// que no llegaba era lo BORRADO, porque `SaveAsync` fusiona (agrega o pisa, nunca saca).
/// Un socio dado de baja en el panel desaparecía de Supabase —`huellas` tiene
/// `on delete cascade`— pero su huella quedaba en toda PC que alguna vez la bajó, y esa
/// persona seguía abriendo la puerta.
/// </summary>
public class SincronizacionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"huella-sync-{Guid.NewGuid():N}.json");
    private const string Gym = "22222222-2222-2222-2222-222222222222";

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private LocalFileStore Store() => new(_path);

    private static StoredTemplate T(string cliente, int uid, string tpl) =>
        new() { ClienteId = cliente, Uid = uid, Template = tpl };

    /// <summary>EL test: al socio que ya no está, se le va la huella de esta PC.</summary>
    [Fact]
    public async Task Un_socio_borrado_en_el_panel_deja_de_abrir_la_puerta_en_esta_PC()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-ANA");
        await store.SaveAsync(Gym, "el-que-se-fue", "T-EX");

        // Lo que manda el servidor al arrancar: el gimnasio ya no lo tiene.
        await store.ReemplazarAsync(Gym, new[] { T("ana", 1, "T-ANA") });

        var db = await store.LoadAsync(Gym);
        Assert.Single(db);
        Assert.DoesNotContain(db, t => t.ClienteId == "el-que-se-fue");
    }

    /// <summary>
    /// La otra mitad del caso de Adriano: las huellas enroladas en la OTRA PC durante esos
    /// días tienen que aparecer acá.
    /// </summary>
    [Fact]
    public async Task Las_huellas_enroladas_en_la_otra_PC_llegan_al_volver()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-ANA");   // lo que esta PC tenía antes de irse

        await store.ReemplazarAsync(Gym, new[]
        {
            T("ana", 1, "T-ANA"),
            T("beto", 2, "T-BETO"),     // enrolados en la otra PC
            T("caro", 3, "T-CARO"),
        });

        var db = await store.LoadAsync(Gym);
        Assert.Equal(3, db.Count);
        Assert.Contains(db, t => t.ClienteId == "beto");
    }

    /// <summary>
    /// Un re-enrolado en la otra PC pisa al viejo, no se duplica. Dos templates para la
    /// misma persona harían que el 1:N tenga dos candidatos del mismo dueño.
    /// </summary>
    [Fact]
    public async Task Una_huella_re_enrolada_en_la_otra_PC_pisa_a_la_vieja()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-VIEJA");

        await store.ReemplazarAsync(Gym, new[] { T("ana", 1, "T-NUEVA") });

        var db = await store.LoadAsync(Gym);
        Assert.Single(db);
        Assert.Equal("T-NUEVA", db[0].Template);
    }

    /// <summary>
    /// Se conserva el uid del SERVIDOR. Así las dos PCs del gimnasio hablan de la misma
    /// persona con el mismo número, y el log de una se puede leer al lado del de la otra.
    /// </summary>
    [Fact]
    public async Task Se_respeta_el_uid_que_manda_el_servidor()
    {
        var store = Store();
        await store.ReemplazarAsync(Gym, new[] { T("ana", 7, "T-ANA"), T("beto", 9, "T-BETO") });

        var db = await store.LoadAsync(Gym);
        Assert.Equal(7, db.First(t => t.ClienteId == "ana").Uid);
        Assert.Equal(9, db.First(t => t.ClienteId == "beto").Uid);
    }

    /// <summary>
    /// Un enrolado POSTERIOR no puede reusar un uid que ya está tomado: la DB en memoria
    /// del SDK indexa por uid y dos personas con el mismo número serían la misma.
    /// </summary>
    [Fact]
    public async Task Un_enrolado_posterior_no_pisa_un_uid_del_servidor()
    {
        var store = Store();
        await store.ReemplazarAsync(Gym, new[] { T("ana", 7, "T-ANA") });

        var uid = await store.SaveAsync(Gym, "nuevo", "T-NUEVO");

        Assert.True(uid > 7, $"el uid {uid} choca con el 7 que ya tiene ana");
    }

    /// <summary>
    /// Un gimnasio no puede quedarse sin huellas porque el lector cambió de gym: reemplazar
    /// toca SOLO al tenant que vino en la respuesta.
    /// </summary>
    [Fact]
    public async Task Reemplazar_un_gimnasio_no_toca_al_otro()
    {
        const string otro = "33333333-3333-3333-3333-333333333333";
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-ANA");
        await store.SaveAsync(otro, "zoe", "T-ZOE");

        await store.ReemplazarAsync(Gym, Array.Empty<StoredTemplate>());

        Assert.Empty(await store.LoadAsync(Gym));
        Assert.Single(await store.LoadAsync(otro));
    }

    /// <summary>
    /// Reemplazar sube la Version, que es la clave con la que el device decide si su DB en
    /// memoria del SDK sigue vigente. Sin esto, el lector seguiría reconociendo con la lista
    /// vieja hasta el próximo enrolado — o sea que el socio borrado seguiría entrando.
    /// </summary>
    [Fact]
    public async Task Reemplazar_invalida_la_DB_cacheada_del_SDK()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-ANA");
        var antes = store.Version;

        // Cambió el template (se re-enroló desde otra PC): tiene que invalidar.
        Assert.True(await store.ReemplazarAsync(Gym, new[] { T("ana", 1, "T-ANA-NUEVA") }));

        Assert.True(store.Version > antes, "la DB del SDK no se iba a rearmar");
    }

    [Fact]
    public async Task Reemplazar_con_lo_mismo_no_toca_nada()
    {
        // Desde la v1.6.1 se reemplaza en cada latido (5 min). Si el servidor dice lo mismo
        // y aun así se subiera la versión, el SDK rearmaría su base cada 5 minutos.
        var store = Store();
        await store.ReemplazarAsync(Gym, new[] { T("ana", 1, "T-ANA") });
        var antes = store.Version;

        Assert.False(await store.ReemplazarAsync(Gym, new[] { T("ana", 1, "T-ANA") }));
        Assert.Equal(antes, store.Version);
    }

    [Fact]
    public async Task Reemplazar_conserva_que_dedo_es_cada_huella()
    {
        var store = Store();
        await store.ReemplazarAsync(Gym, new[]
        {
            new StoredTemplate { ClienteId = "ana", Uid = 1, Template = "PRINCIPAL", Dedo = 1 },
            new StoredTemplate { ClienteId = "ana", Uid = 2, Template = "RESPALDO", Dedo = 2 },
        });

        // Re-enrolar el principal no puede pisar el respaldo.
        await store.SaveAsync(Gym, "ana", "PRINCIPAL-NUEVO", dedo: 1);
        var todas = await store.LoadAsync(Gym);
        Assert.Contains(todas, t => t.Dedo == 2 && t.Template == "RESPALDO");
        Assert.Contains(todas, t => t.Dedo == 1 && t.Template == "PRINCIPAL-NUEVO");
    }

    [Fact]
    public async Task Quitar_saca_la_huella_y_la_puerta_deja_de_usarla()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "P", dedo: 1);
        await store.SaveAsync(Gym, "ana", "R", dedo: 2);
        await store.SaveAsync(Gym, "beto", "B");
        var antes = store.Version;

        Assert.Equal(1, await store.QuitarAsync(Gym, "ana", dedo: 2));
        Assert.True(store.Version > antes);   // la DB del SDK se rearma: deja de abrir YA
        Assert.Equal(2, (await store.LoadAsync(Gym)).Count);

        Assert.Equal(1, await store.QuitarAsync(Gym, "ana"));   // sin dedo = todos los de ana
        Assert.Single(await store.LoadAsync(Gym));
    }

    private const string OtroGym = "33333333-3333-3333-3333-333333333333";

    /// <summary>
    /// Mover el lector de un gimnasio a otro tiene que LIMPIAR las huellas del anterior.
    ///
    /// `ReemplazarAsync` solo pisa la lista del tenant que recibe, asi que las del gimnasio
    /// viejo se quedaban en `templates.json` para siempre. Son datos biometricos (Ley 29733)
    /// de socios de un gimnasio que ya no es dueno de esa computadora, y viajan con la PC si
    /// se vende o se reasigna. Visto de verdad el 26-sep: re-vinculada de Villa Periodista a
    /// Sede 2, la PC seguia con las 23 huellas de Villa Periodista en el disco.
    /// </summary>
    [Fact]
    public async Task Al_mover_el_lector_a_otro_gimnasio_se_van_las_huellas_del_anterior()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-ANA");
        await store.SaveAsync(Gym, "beto", "T-BETO");
        await store.SaveAsync(OtroGym, "carla", "T-CARLA");

        await store.OlvidarOtrosAsync(OtroGym);

        Assert.Empty(await store.LoadAsync(Gym));
        Assert.Single(await store.LoadAsync(OtroGym));
    }

    /// <summary>
    /// Y el numero que ve super-admin es el del gimnasio, no el del archivo.
    ///
    /// `CountAsync()` sumaba siempre todos los tenants. En una PC de un solo gimnasio da
    /// igual —por eso no se noto— pero apenas una PC pasa por dos gimnasios, el "huellas
    /// cargadas" de cada lector miente: el 26-sep decia 25 con Sede 2, que tiene 2.
    /// </summary>
    [Fact]
    public async Task Las_huellas_cargadas_son_las_de_ese_gimnasio()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-ANA");
        await store.SaveAsync(Gym, "beto", "T-BETO");
        await store.SaveAsync(OtroGym, "carla", "T-CARLA");

        Assert.Equal(2, await store.CountAsync(Gym));
        Assert.Equal(1, await store.CountAsync(OtroGym));
        Assert.Equal(3, await store.CountAsync());   // sin tenant, sigue siendo todo el archivo
    }

    /// <summary>
    /// "Volver a vincular" al MISMO gimnasio no puede borrar nada.
    ///
    /// Es el boton que la tarjeta de Configuracion ofrece como arreglo para todo ("re-vincular
    /// es idempotente, barato, y es lo unico que arregla"), asi que se aprieta seguido y a
    /// ciegas. Si `OlvidarOtrosAsync` se llevara puesto al tenant que conserva, ese clic
    /// vaciaria el lector del gimnasio y nadie podria entrar hasta re-enrolar a todos.
    /// </summary>
    [Fact]
    public async Task Volver_a_vincular_al_mismo_gimnasio_no_borra_sus_huellas()
    {
        var store = Store();
        await store.SaveAsync(Gym, "ana", "T-ANA");
        await store.SaveAsync(Gym, "beto", "T-BETO");

        await store.OlvidarOtrosAsync(Gym);
        await store.OlvidarOtrosAsync(Gym);   // dos clics seguidos, que es lo que pasa de verdad

        Assert.Equal(2, await store.CountAsync(Gym));
    }
}

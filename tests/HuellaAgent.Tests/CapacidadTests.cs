using System.Diagnostics;
using HuellaAgent.Storage;
using Xunit;
using Xunit.Abstractions;

namespace HuellaAgent.Tests;

/// <summary>
/// ¿Cuántas huellas aguanta un gimnasio antes de que esto se ponte lento?
///
/// La pregunta salió el 23-sep: "¿1000? ¿10000?". No había ningún número medido en ningún
/// lado, así que esto lo mide. Hoy el gimnasio más grande del sistema tiene **1.489 socios**
/// (Essential Huaycán), y el segundo 1.443 (RQ Fitness): ese es el tamaño que de verdad hay
/// que aguantar, no diez mil.
///
/// ⚠️ ESTO MIDE EL LADO .NET, no el SDK del lector. El 1:N lo hace `zkfp2.DBIdentify` en
/// memoria nativa, y ese límite hay que medirlo con el aparato en la mano — está anotado
/// como pendiente en el PRD 103.
///
/// Lo que sí se ve acá es dónde se pone incómodo el almacenamiento local, que es lo que
/// toca CADA enrolado: `SaveAsync` lee el archivo entero, lo modifica y lo reescribe.
/// </summary>
public class CapacidadTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"huella-cap-{Guid.NewGuid():N}.json");
    private const string Gym = "22222222-2222-2222-2222-222222222222";

    // Un template real del SLK20R ronda los 2 KB en base64. Se usa uno de ese tamaño para
    // que el archivo pese lo que va a pesar de verdad.
    private static readonly string Template = Convert.ToBase64String(new byte[1536]);

    public CapacidadTests(ITestOutputHelper salida) => _out = salida;

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private async Task<LocalFileStore> ConHuellas(int cuantas)
    {
        var store = new LocalFileStore(_path);
        var items = Enumerable.Range(1, cuantas)
            .Select(i => new StoredTemplate { ClienteId = $"socio-{i}", Uid = i, Template = Template })
            .ToList();
        await store.ReemplazarAsync(Gym, items);
        return store;
    }

    /// <summary>
    /// El tamaño que de verdad importa: el gimnasio más grande que existe hoy en el sistema.
    /// Si esto no fuera cómodo, el lector no serviría para los clientes que ya tenemos.
    /// </summary>
    [Fact]
    public async Task Con_1500_huellas_enrolar_y_leer_siguen_siendo_instantaneos()
    {
        var store = await ConHuellas(1500);

        var leer = Stopwatch.StartNew();
        var db = await store.LoadAsync(Gym);
        leer.Stop();

        var guardar = Stopwatch.StartNew();
        await store.SaveAsync(Gym, "socio-nuevo", Template);
        guardar.Stop();

        var mb = new FileInfo(_path).Length / 1024d / 1024d;
        _out.WriteLine($"1.500 huellas · archivo {mb:F1} MB · leer {leer.ElapsedMilliseconds} ms · enrolar {guardar.ElapsedMilliseconds} ms");

        Assert.Equal(1500, db.Count);
        // Un enrolado reescribe el archivo entero. Medio segundo es el techo de lo que
        // alguien tolera parado en el mostrador.
        Assert.True(guardar.ElapsedMilliseconds < 500, $"enrolar tardo {guardar.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Diez mil es el número que preguntó Adriano. No hay ningún gimnasio así hoy —el mayor
    /// tiene 1.489— pero conviene saber dónde está la pared antes de prometer nada.
    /// </summary>
    [Fact]
    public async Task Con_10000_huellas_se_mide_donde_empieza_a_doler()
    {
        var store = await ConHuellas(10_000);

        var leer = Stopwatch.StartNew();
        var db = await store.LoadAsync(Gym);
        leer.Stop();

        var guardar = Stopwatch.StartNew();
        await store.SaveAsync(Gym, "socio-nuevo", Template);
        guardar.Stop();

        var mb = new FileInfo(_path).Length / 1024d / 1024d;
        _out.WriteLine($"10.000 huellas · archivo {mb:F1} MB · leer {leer.ElapsedMilliseconds} ms · enrolar {guardar.ElapsedMilliseconds} ms");

        Assert.Equal(10_000, db.Count);
        // Sin aserción de tiempo a propósito: acá el objetivo es MEDIR, no fijar un techo
        // para un tamaño que todavía no existe. El número queda en la salida del test.
    }

    /// <summary>
    /// El identify lee el store en CADA vuelta del portero. Si eso escalara con la cantidad
    /// de socios, un gimnasio grande tendría la puerta cada vez más lenta.
    /// </summary>
    [Fact]
    public async Task Leer_para_identificar_no_se_encarece_con_la_cantidad_de_socios()
    {
        var store = await ConHuellas(5000);
        await store.LoadAsync(Gym);   // primera lectura: llena el cache en memoria

        var reloj = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) await store.LoadAsync(Gym);
        reloj.Stop();

        var porVuelta = reloj.Elapsed.TotalMilliseconds / 50;
        _out.WriteLine($"5.000 huellas · leer para identificar: {porVuelta:F1} ms por vuelta");

        // El cache en memoria existe justamente para esto: antes cada poll releia el archivo
        // de disco y deserializaba todo, con costo lineal en la cantidad de socios.
        Assert.True(porVuelta < 50, $"cada identify cuesta {porVuelta:F1} ms solo en leer el store");
    }
}

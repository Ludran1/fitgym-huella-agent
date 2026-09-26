using System.Text.Json;
using HuellaAgent.Config;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Lo que sale en el ZIP que instala un gimnasio.
///
/// POR QUE HAY TESTS DE UN ARCHIVO DE CONFIGURACION. Hasta el 23-sep el paquete se armaba
/// a mano, y dos de esos pasos rompen en SILENCIO:
///
///   · `appsettings.json` con `UseRealDevice` en false → el agente arranca con MockDevice
///     en todos los gimnasios que instalen ese ZIP. Queda vivo, /health contesta, y solo
///     el campo `device` delata que no hay lector de verdad.
///   · `version.txt` mal → launch.ps1 lo compara contra el tag del ultimo release para
///     auto-actualizar, y su try/catch se traga cualquier error. Los gimnasios se quedan
///     en la version vieja para siempre y nadie se entera.
///
/// Ninguno de los dos se ve mirando el ZIP. Los dos se ven acá, en cinco milisegundos.
/// </summary>
public class PaqueteTests
{
    /// <summary>Sube desde bin/Debug/netX hasta la raiz del repo (la que tiene el .sln).</summary>
    private static string Raiz()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "HuellaAgent.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static string ConfigDeProduccion() =>
        Path.Combine(Raiz(), "deploy", "appsettings.produccion.json");

    [Fact]
    public void La_config_de_produccion_existe_y_es_json_valido()
    {
        var ruta = ConfigDeProduccion();
        Assert.True(File.Exists(ruta), $"falta {ruta}: es lo que se instala en la PC del gimnasio");
        var ex = Record.Exception(() => JsonDocument.Parse(File.ReadAllText(ruta)));
        Assert.Null(ex);
    }

    /// <summary>
    /// EL test de este archivo. El default de `UseRealDevice` es false a proposito —para que
    /// los tests y el desarrollo corran sin lector— y por eso lo que se publica TIENE que
    /// decir lo contrario, explicitamente.
    /// </summary>
    [Fact]
    public void Lo_que_se_publica_usa_el_lector_de_verdad_y_no_el_simulado()
    {
        var cfg = new ConfigurationBuilder().AddJsonFile(ConfigDeProduccion()).Build();
        var agente = AgentConfig.Load(cfg);

        Assert.True(agente.UseRealDevice,
            "el ZIP publicado arrancaria con MockDevice: el lector no leeria nada en ningun gimnasio");
        Assert.Equal(8000, agente.Port);   // huellaApi.ts lo tiene fijo en localhost:8000
    }

    /// <summary>
    /// La config de produccion NO puede traer la configuracion de UN gimnasio.
    ///
    /// Si el ZIP saliera con el torniquete prendido o con un puerto COM adentro, ese valor
    /// se instalaria en gimnasios que no tienen torniquete — y peor: haria creer que la
    /// configuracion por gym se resuelve empaquetando, que es justo lo que HU-17 viene a
    /// sacar. Lo de cada gimnasio viaja con el vinculo, como ya viajan las huellas.
    /// </summary>
    [Fact]
    public void La_config_de_produccion_no_trae_nada_de_un_gimnasio_puntual()
    {
        var raiz = JsonDocument.Parse(File.ReadAllText(ConfigDeProduccion())).RootElement;
        var agente = raiz.GetProperty("Agent");

        foreach (var clave in new[] { "TurnstileEnabled", "RelayPort", "AutoDecide", "KioskToken", "SupabaseUrl", "SupabaseAnonKey", "ApiKey" })
            Assert.False(agente.TryGetProperty(clave, out _),
                $"'{clave}' es de UN gimnasio (o un secreto) y no puede viajar en el ZIP que instalan todos");
    }

    /// <summary>
    /// El agente se publica self-contained: la PC del gimnasio no tiene .NET y pedirle que
    /// lo instale seria otro paso manual, justo lo que se esta sacando. Y single-file,
    /// porque launch.ps1 se auto-actualiza reemplazando UN archivo (HuellaAgent.exe): con
    /// el publish repartido en DLLs, el updater dejaria un exe nuevo con dependencias
    /// viejas. Los dos viven en publicar.ps1; esto evita que alguien los saque sin querer.
    /// </summary>
    [Fact]
    public void El_script_de_publicacion_mantiene_self_contained_y_un_solo_archivo()
    {
        var script = File.ReadAllText(Path.Combine(Raiz(), "scripts", "publicar.ps1"));

        Assert.Contains("--self-contained true", script);
        Assert.Contains("PublishSingleFile=true", script);
        Assert.Contains("-r win-x64", script);
        // Sin esto el ZIP no lleva la config de produccion y queda con la de desarrollo.
        Assert.Contains("appsettings.produccion.json", script);
        // Sin esto launch.ps1 no puede decidir si hay que actualizar.
        Assert.Contains("version.txt", script);
    }

    /// <summary>
    /// La version del csproj es la que termina en el FileVersion del exe y la que
    /// `publicar.ps1` escribe en version.txt. Que sea legible y tenga forma de version es
    /// lo unico que hace falta acá; que coincida con el tag del release lo verifica el CI.
    /// </summary>
    [Fact]
    public void El_csproj_declara_una_version_con_forma_de_version()
    {
        var csproj = File.ReadAllText(Path.Combine(Raiz(), "src", "HuellaAgent", "HuellaAgent.csproj"));
        var m = System.Text.RegularExpressions.Regex.Match(csproj, @"<Version>([^<]+)</Version>");

        Assert.True(m.Success, "el csproj no declara <Version>: publicar.ps1 la necesita para version.txt");
        Assert.True(Version.TryParse(m.Groups[1].Value.Trim(), out _),
            $"<Version> dice '{m.Groups[1].Value}' y launch.ps1 la parsea con [version]");
    }

    /// <summary>
    /// El agente tiene que leer su appsettings.json de AL LADO DEL EXE, no del directorio
    /// actual. `CreateBuilder(args)` a secas usa Directory.GetCurrentDirectory(), y eso hacia
    /// que la config del gimnasio desapareciera en silencio cuando el proceso arrancaba desde
    /// otra carpeta: UseRealDevice volvia a false (lector simulado diciendo "connected"),
    /// sin torniquete y sin pulso. Funcionaba en recepcion solo porque run-hidden.vbs fija el
    /// CurrentDirectory antes de llamar a launch.ps1; cualquier otro camino lo rompia.
    /// Paso el 26-sep corriendo launch.ps1 a mano.
    /// </summary>
    [Fact]
    public void El_agente_lee_su_config_al_lado_del_exe()
    {
        var program = File.ReadAllText(Path.Combine(Raiz(), "src", "HuellaAgent", "Program.cs"));
        // Sin los comentarios: esta misma explicacion nombra el CreateBuilder(args) que prohibe.
        var codigo = string.Join("\n", program.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        Assert.Contains("ContentRootPath = AppContext.BaseDirectory", codigo);
        Assert.DoesNotContain("CreateBuilder(args)", codigo);
    }

    /// <summary>
    /// Y launch.ps1 lo arranca con el directorio de trabajo puesto igual. Es redundante con
    /// el ContentRootPath a proposito: son las dos unicas cosas que pueden hacer que un
    /// gimnasio corra con lector simulado sin que nadie lo note.
    /// </summary>
    [Fact]
    public void Launch_arranca_el_agente_en_su_propia_carpeta()
    {
        var script = File.ReadAllText(Path.Combine(Raiz(), "scripts", "windows", "launch.ps1"));

        var arranques = script.Split("Start-Process").Length - 1;
        Assert.Equal(2, arranques);   // el normal y el de la vuelta atras
        Assert.Equal(arranques, script.Split("-WorkingDirectory $dir").Length - 1);
    }
}

using HuellaAgent.Relays;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// El puerto del relé, sin abrir el Administrador de dispositivos.
///
/// Era el último dato de la instalación que obligaba a que alguien fuera hasta la PC: para
/// el torniquete había que buscar en "Puertos (COM y LPT)" cuál le tocó al adaptador y
/// escribirlo a mano en un archivo de texto. Un recepcionista no hace eso.
///
/// Y es el único de toda la configuración que NO se puede resolver desde el servidor: el
/// número depende de esa máquina y de en qué USB lo enchufaron.
/// </summary>
public class PuertoReleTests
{
    private static PuertoElegido Elegir(string? configurado, string[] puertos, bool ch340) =>
        PuertoRele.Elegir(configurado, () => puertos, () => ch340);

    /// <summary>
    /// Lo escrito a mano gana, y es la salida para el gimnasio donde la detección no alcance
    /// (varios puertos serie). Pero tiene que EXISTIR — ver el test de abajo.
    /// </summary>
    [Fact]
    public void Lo_configurado_a_mano_le_gana_a_la_deteccion()
    {
        var r = Elegir("COM7", new[] { "COM3", "COM4", "COM7" }, ch340: true);

        Assert.Equal("COM7", r.Port);
        Assert.Equal(OrigenDelPuerto.Configurado, r.Origen);
    }

    /// <summary>
    /// EL caso de campo del 23-sep. Windows le cambia el numero al adaptador cada vez que se
    /// lo reenchufa: una PC configurada con COM3 volvio como COM4 y el torniquete dejo de
    /// abrir. La deteccion lo habria encontrado al instante, pero quedaba bloqueada
    /// justamente por ese valor viejo.
    ///
    /// "Respetar lo que alguien configuro" no puede significar obedecer un dato que ya no
    /// apunta a nada.
    /// </summary>
    [Fact]
    public void Un_puerto_configurado_que_ya_no_existe_no_bloquea_la_deteccion()
    {
        var r = Elegir("COM3", new[] { "COM4" }, ch340: true);

        Assert.Equal("COM4", r.Port);
        Assert.Equal(OrigenDelPuerto.Detectado, r.Origen);
        Assert.Contains("COM3", r.Motivo);   // queda dicho, para que nadie lo persiga
    }

    /// <summary>
    /// Pero con VARIOS puertos y el configurado ausente, sigue sin adivinar: ahi el dato a
    /// mano era justamente lo que desempataba, y perderlo no habilita a elegir al azar.
    /// </summary>
    [Fact]
    public void Un_configurado_ausente_con_varios_puertos_sigue_sin_adivinar()
    {
        var r = Elegir("COM3", new[] { "COM4", "COM5" }, ch340: true);

        Assert.Null(r.Port);
        Assert.Equal(OrigenDelPuerto.Ninguno, r.Origen);
    }

    /// <summary>El caso normal de una PC de recepción: un relé, un puerto.</summary>
    [Fact]
    public void Un_solo_puerto_y_un_CH340_enchufado_es_el_rele()
    {
        var r = Elegir(null, new[] { "COM3" }, ch340: true);

        Assert.Equal("COM3", r.Port);
        Assert.Equal(OrigenDelPuerto.Detectado, r.Origen);
    }

    /// <summary>
    /// Un solo puerto pero sin confirmar el chip: se usa igual —es lo único que hay— pero
    /// queda marcado distinto. El módulo podría traer otro chip que el CH340; el comentario
    /// de UsbRelay ya avisa que los bytes hay que verificarlos contra el módulo real.
    /// </summary>
    [Fact]
    public void Un_solo_puerto_sin_CH340_se_usa_pero_avisando()
    {
        var r = Elegir(null, new[] { "COM3" }, ch340: false);

        Assert.Equal("COM3", r.Port);
        Assert.Equal(OrigenDelPuerto.Unico, r.Origen);
    }

    /// <summary>
    /// EL test de este archivo: con varios puertos NO adivina.
    ///
    /// Elegir mal significa escribirle bytes de control a otro aparato conectado a esa PC.
    /// Es mejor pedir ayuda en el 1% de las instalaciones que pulsar el relé equivocado en
    /// el 100%. El motivo lista los puertos, así quien tenga que decidir ve las opciones.
    /// </summary>
    [Fact]
    public void Con_varios_puertos_no_adivina()
    {
        var r = Elegir(null, new[] { "COM3", "COM5" }, ch340: true);

        Assert.Null(r.Port);
        Assert.Equal(OrigenDelPuerto.Ninguno, r.Origen);
        Assert.Contains("COM3", r.Motivo);
        Assert.Contains("COM5", r.Motivo);
        Assert.Contains("RelayPort", r.Motivo);
    }

    [Fact]
    public void Sin_ningun_puerto_lo_dice_y_no_inventa_uno()
    {
        var r = Elegir(null, Array.Empty<string>(), ch340: false);

        Assert.Null(r.Port);
        Assert.Equal(OrigenDelPuerto.Ninguno, r.Origen);
        Assert.Contains("no está enchufado", r.Motivo);
    }

    /// <summary>
    /// El caso que más confunde en una instalación: el aparato está, pero Windows no le dio
    /// un puerto porque falta el driver del CH340. Sin este mensaje, "no hay puerto" y
    /// "falta el driver" se ven igual, y son arreglos distintos.
    /// </summary>
    [Fact]
    public void Un_CH340_sin_puerto_apunta_al_driver_y_no_al_cable()
    {
        var r = Elegir(null, Array.Empty<string>(), ch340: true);

        Assert.Null(r.Port);
        Assert.Contains("driver", r.Motivo);
    }

    /// <summary>Un valor en blanco no cuenta como configurado: es el campo vacío del JSON.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Un_RelayPort_vacio_dispara_la_deteccion(string? vacio)
    {
        var r = Elegir(vacio, new[] { "COM3" }, ch340: true);

        Assert.Equal(OrigenDelPuerto.Detectado, r.Origen);
    }

    /// <summary>En Linux/CI no hay cfgmgr32: tiene que devolver false, no explotar.</summary>
    [Fact]
    public void Preguntar_por_el_CH340_nunca_tira_una_excepcion()
    {
        var ex = Record.Exception(() => PuertoRele.HayCh340());
        Assert.Null(ex);
    }
}

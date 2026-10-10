using System.Text;
using HuellaAgent.Config;
using HuellaAgent.Devices;
using HuellaAgent.Qr;
using HuellaAgent.Relays;
using HuellaAgent.Supabase;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// El lector QR de mostrador leído por el agente (v1.8, 10-oct), por partes y sin hardware:
/// cómo se arman los códigos con lo que llega por el puerto, qué puerto es del lector (y
/// nunca el del relé), y que la puerta atienda de a uno aunque lleguen un dedo y un QR juntos.
/// </summary>
public class LectorQrTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 15, 0, 0, DateTimeKind.Utc);

    private static List<string> Leer(CodigosDelLector c, string texto, DateTime cuando)
    {
        var b = Encoding.Latin1.GetBytes(texto);
        return c.Agregar(b, b.Length, cuando);
    }

    // ── Armar los códigos ───────────────────────────────────────────────────────

    [Fact]
    public void Un_codigo_cerrado_con_CR_sale_entero()
    {
        var c = new CodigosDelLector();
        Assert.Equal(new[] { "FIT-6611-H7AE6TQV" }, Leer(c, "FIT-6611-H7AE6TQV\r", T0));
    }

    [Fact]
    public void CR_y_LF_juntos_no_arman_un_segundo_codigo_vacio()
    {
        var c = new CodigosDelLector();
        Assert.Single(Leer(c, "FIT-6611-H7AE6TQV\r\n", T0));
        Assert.Null(c.Vencido(T0.AddSeconds(1)));
    }

    /// <summary>El puerto entrega en pedazos: un mismo código puede llegar en dos lecturas.</summary>
    [Fact]
    public void Un_codigo_partido_en_dos_lecturas_sale_uno_solo()
    {
        var c = new CodigosDelLector();
        Assert.Empty(Leer(c, "FIT-66", T0));
        Assert.Equal(new[] { "FIT-6611-H7AE6TQV" }, Leer(c, "11-H7AE6TQV\r", T0.AddMilliseconds(5)));
    }

    /// <summary>Hay lectores que de fábrica no mandan ningún cierre: lo cierra el silencio.</summary>
    [Fact]
    public void Sin_cierre_lo_entrega_el_silencio()
    {
        var c = new CodigosDelLector();
        Assert.Empty(Leer(c, "FIT-6611-H7AE6TQV", T0));
        Assert.Null(c.Vencido(T0.AddMilliseconds(CodigosDelLector.SilencioMs / 2)));
        Assert.Equal("FIT-6611-H7AE6TQV", c.Vencido(T0.AddMilliseconds(CodigosDelLector.SilencioMs + 1)));
        Assert.Null(c.Vencido(T0.AddSeconds(5)));   // se entrega una sola vez
    }

    [Fact]
    public void Los_caracteres_de_control_se_descartan()
    {
        var c = new CodigosDelLector();
        Assert.Equal(new[] { "FIT-1234-ABCDEFGH" }, Leer(c, "\u0002FIT-1234-ABCDEFGH\u0003\r", T0));
    }

    [Fact]
    public void Un_byte_suelto_no_es_un_codigo()
    {
        var c = new CodigosDelLector();
        Assert.Empty(Leer(c, "A\r", T0));
    }

    [Fact]
    public void Dos_codigos_en_una_misma_lectura_salen_los_dos()
    {
        var c = new CodigosDelLector();
        Assert.Equal(new[] { "FIT-AAAAAAAA", "FIT-BBBBBBBB" }, Leer(c, "FIT-AAAAAAAA\rFIT-BBBBBBBB\r", T0));
    }

    /// <summary>El código abre la puerta como una llave: al log va lo justo para reconocerlo.</summary>
    [Fact]
    public void Al_log_no_va_el_codigo_entero()
    {
        var m = LectorQrService.Enmascarado("FIT-6611-H7AE6TQV");
        Assert.DoesNotContain("H7AE6TQV", m);
        Assert.StartsWith("FIT-6611", m);
        Assert.Contains("17", m);
    }

    // ── Qué puerto es del lector ────────────────────────────────────────────────

    private static readonly PuertoUsb Netum = new("COM5", "VID_9901", "PID_0302");
    private static readonly PuertoUsb Rele = new("COM3", PuertoRele.VidCh340, "PID_7523");

    [Fact]
    public void Reconoce_el_lector_por_el_fabricante()
    {
        var r = PuertoLectorQr.Elegir(null, new[] { Netum, Rele }, new[] { "COM3", "COM5" });
        Assert.Equal("COM5", r.Puerto);
    }

    [Fact]
    public void Nunca_toma_el_puerto_del_rele()
    {
        var r = PuertoLectorQr.Elegir(null, new[] { Rele }, new[] { "COM3" });
        Assert.Null(r.Puerto);
    }

    /// <summary>
    /// Un puerto serie lo usa un programa por vez: abrir el de otro aparato lo dejaría sin
    /// andar mientras corra el agente. Pero el motivo lo nombra, para poder sumarlo.
    /// </summary>
    [Fact]
    public void No_abre_un_aparato_que_no_conoce_aunque_sea_el_unico()
    {
        var otro = new PuertoUsb("COM7", "VID_0403", "PID_6001");
        var r = PuertoLectorQr.Elegir(null, new[] { otro }, new[] { "COM7" });
        Assert.Null(r.Puerto);
        Assert.Contains("COM7 VID_0403&PID_6001", r.Motivo);
    }

    [Fact]
    public void El_puerto_escrito_a_mano_gana_si_existe()
    {
        var r = PuertoLectorQr.Elegir("com8", Array.Empty<PuertoUsb>(), new[] { "COM8" });
        Assert.Equal("COM8", r.Puerto);
    }

    [Fact]
    public void El_puerto_a_mano_que_ya_no_existe_no_frena_la_deteccion()
    {
        var r = PuertoLectorQr.Elegir("COM8", new[] { Netum }, new[] { "COM5" });
        Assert.Equal("COM5", r.Puerto);
        Assert.Contains("COM8", r.Motivo);
    }

    [Fact]
    public void Un_puerto_que_Windows_ya_no_ve_no_cuenta()
    {
        var r = PuertoLectorQr.Elegir(null, new[] { Netum }, Array.Empty<string>());
        Assert.Null(r.Puerto);
    }

    [Theory]
    [InlineData(@"USB\VID_9901&PID_0301\A-00000", "VID_9901", "PID_0301")]
    [InlineData(@"USB\VID_1A86&PID_7523&MI_00\6&1F6229D8&0&0000", "VID_1A86", "PID_7523")]
    [InlineData(@"USB\ROOT_HUB30\4&37092B88&0&0", null, null)]
    public void VID_y_PID_salen_del_identificador_de_Windows(string id, string? vid, string? pid)
    {
        Assert.Equal((vid, pid), PuertosSerieUsb.VidPid(id));
    }

    // ── El relé, con el lector QR enchufado ─────────────────────────────────────

    /// <summary>
    /// Lo que se corrigió en la v1.8: la regla "el único puerto serie es el relé" agarraba el
    /// del lector. Sin los del lector, el relé vuelve a ser el único.
    /// </summary>
    [Fact]
    public void Con_el_lector_enchufado_el_rele_sigue_siendo_el_unico_puerto()
    {
        var sinLector = PuertoLectorQr.SinLosDelLector(new[] { "COM3", "COM5" }, new[] { Netum, Rele }, null);
        var r = PuertoRele.Elegir(null, () => sinLector, () => false, () => Array.Empty<string>());
        Assert.Equal("COM3", r.Port);
    }

    [Fact]
    public void Sin_rele_no_le_escribe_al_lector()
    {
        var sinLector = PuertoLectorQr.SinLosDelLector(new[] { "COM5" }, new[] { Netum }, null);
        var r = PuertoRele.Elegir(null, () => sinLector, () => false, () => Array.Empty<string>());
        Assert.Null(r.Port);
    }

    [Fact]
    public void El_puerto_del_lector_escrito_a_mano_tampoco_es_del_rele()
    {
        var sinLector = PuertoLectorQr.SinLosDelLector(new[] { "COM3", "COM9" }, Array.Empty<PuertoUsb>(), "COM9");
        Assert.Equal(new[] { "COM3" }, sinLector);
    }

    // ── La puerta: de a uno ─────────────────────────────────────────────────────

    private sealed class ReleQueAnota : IRelay
    {
        public readonly List<(DateTime Desde, DateTime Hasta)> Pulsos = new();
        public bool IsConnected => true;
        public string? LastError => null;
        public bool TryConnect() => true;

        public async Task PulseAsync(int pulseMs, CancellationToken ct)
        {
            var desde = DateTime.UtcNow;
            await Task.Delay(pulseMs, ct);
            lock (Pulsos) Pulsos.Add((desde, DateTime.UtcNow));
        }
    }

    private static (Puerta Puerta, ReleQueAnota Rele, Bitacora Bitacora) NuevaPuerta(bool torniquete = true)
    {
        var cfg = new AgentConfig { TurnstileEnabled = torniquete, RelayPulseMs = 150, SonidoEnabled = false };
        var rele = new ReleQueAnota();
        var bitacora = new Bitacora();
        var puerta = new Puerta(rele, new Bocina(cfg, NullLogger<Bocina>.Instance),
            new ConfigDelGimnasio(cfg, NullLogger<ConfigDelGimnasio>.Instance), bitacora, NullLogger<Puerta>.Instance);
        return (puerta, rele, bitacora);
    }

    private static HuellaRpc.Veredicto Pasa(string nombre) =>
        new(Permitido: true, AbrirPuerta: true, Motivo: null, Tipo: "socio", Nombre: nombre, Mensaje: "Bienvenido", YaHoy: false);

    /// <summary>
    /// Un dedo y un QR al mismo tiempo: cada uno tiene su pulso entero. Pisándose, el
    /// "cerrar" del primero le cortaba el giro al segundo.
    /// </summary>
    [Fact]
    public async Task Un_dedo_y_un_QR_al_mismo_tiempo_no_se_pisan_el_pulso()
    {
        var (puerta, rele, _) = NuevaPuerta();

        await Task.WhenAll(
            puerta.AtenderAsync(Pasa("Ana"), "huella", 600, CancellationToken.None),
            puerta.AtenderAsync(Pasa("Beto"), "qr", null, CancellationToken.None));

        var p = rele.Pulsos.OrderBy(x => x.Desde).ToArray();
        Assert.Equal(2, p.Length);
        Assert.True(p[1].Desde >= p[0].Hasta,
            $"el segundo pulso empezó {(p[0].Hasta - p[1].Desde).TotalMilliseconds} ms antes de que termine el primero");
    }

    [Fact]
    public async Task El_QR_que_pasa_abre_y_queda_anotado_como_qr()
    {
        var (puerta, rele, bitacora) = NuevaPuerta();
        await puerta.AtenderAsync(Pasa("Ana"), "qr", null, CancellationToken.None);

        Assert.Single(rele.Pulsos);
        var e = bitacora.Ultimos().Last();
        Assert.Equal(("paso", "qr", "Ana"), (e.Que, e.Metodo, e.Quien));
    }

    [Fact]
    public async Task Un_QR_rechazado_no_abre()
    {
        var (puerta, rele, bitacora) = NuevaPuerta();
        var vencido = new HuellaRpc.Veredicto(true, false, "expired", "socio", "Ana", "Su plan vencio", false);
        await puerta.AtenderAsync(vencido, "qr", null, CancellationToken.None);

        Assert.Empty(rele.Pulsos);
        var e = bitacora.Ultimos().Last();
        Assert.Equal(("rechazado", "expired"), (e.Que, e.Motivo));
    }

    [Fact]
    public async Task Sin_respuesta_del_servidor_no_abre()
    {
        var (puerta, rele, bitacora) = NuevaPuerta();
        await puerta.AtenderAsync(null, "qr", null, CancellationToken.None);

        Assert.Empty(rele.Pulsos);
        Assert.Equal("sin_servidor", bitacora.Ultimos().Last().Que);
    }

    [Fact]
    public async Task En_un_gym_sin_torniquete_solo_registra()
    {
        var (puerta, rele, bitacora) = NuevaPuerta(torniquete: false);
        await puerta.AtenderAsync(Pasa("Ana"), "qr", null, CancellationToken.None);

        Assert.Empty(rele.Pulsos);
        Assert.Equal("paso", bitacora.Ultimos().Last().Que);
    }
}

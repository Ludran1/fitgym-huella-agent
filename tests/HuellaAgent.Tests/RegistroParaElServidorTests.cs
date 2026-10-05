using HuellaAgent.Devices;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// El registro que viaja al servidor en el latido (lector_registros, 48 h). Nace del 3 al 5
/// de octubre: recepción rechazaba a casi todos y el registro que lo explicaba estaba sólo
/// en esa PC.
/// </summary>
public class RegistroParaElServidorTests
{
    private static (Logger log, RegistroParaElServidor reg) Armar()
    {
        var reg = new RegistroParaElServidor();
        var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(reg).CreateLogger();
        return (log, reg);
    }

    [Fact]
    public void Manda_las_lecturas_del_agente_y_no_el_ruido_de_NET()
    {
        var (log, reg) = Armar();
        log.ForContext(Constants.SourceContextPropertyName, "Identify")
           .Information("identify: match uid={Uid} score={Score}", 3, 763);
        log.ForContext(Constants.SourceContextPropertyName, "Microsoft.Hosting.Lifetime")
           .Information("Application started");
        log.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Server")
           .Warning("algo raro en Kestrel");
        log.Debug("detalle que nadie necesita");

        var p = reg.Pendientes(100);
        Assert.Equal(2, p.Count);
        Assert.Contains(p, l => l.Mensaje == "identify: match uid=3 score=763" && l.Nivel == "INF");
        Assert.Contains(p, l => l.Nivel == "WRN");   // de .NET, sólo avisos y errores
    }

    [Fact]
    public void Un_aviso_repetido_se_suma_en_vez_de_ocupar_otra_linea()
    {
        var (log, reg) = Armar();
        for (var i = 1; i <= 540; i++)
            log.Warning("No se pudo abrir el lector ({N} intentos): {Motivo}", i * 30, "Windows no ve ningun lector");

        var p = reg.Pendientes(100);
        Assert.Single(p);
        Assert.Equal(540, p[0].Repeticiones);
        Assert.Contains("16200 intentos", p[0].Mensaje);   // el texto más reciente
        Assert.NotNull(p[0].HastaUtc);
    }

    [Fact]
    public void Dos_lecturas_seguidas_NO_se_agrupan()
    {
        // Dos "match" seguidos son dos personas distintas: agruparlos borraría a una.
        var (log, reg) = Armar();
        log.Information("identify: match uid={Uid} score={Score}", 3, 763);
        log.Information("identify: match uid={Uid} score={Score}", 4, 596);

        Assert.Equal(2, reg.Pendientes(100).Count);
    }

    [Fact]
    public void Si_el_latido_falla_las_lineas_se_vuelven_a_mandar()
    {
        var (log, reg) = Armar();
        log.Information("identify: dedo leido SIN coincidencia ({N} huellas cargadas)", 0);

        var primero = reg.Pendientes(100);   // el latido falló: no se confirma
        var segundo = reg.Pendientes(100);

        Assert.Single(primero);
        Assert.Single(segundo);

        reg.Confirmar(segundo);              // ahora sí lo tomó
        Assert.Empty(reg.Pendientes(100));
    }

    [Fact]
    public void Repetidos_que_llegan_mientras_el_latido_viaja_no_se_pierden()
    {
        var (log, reg) = Armar();
        for (var i = 0; i < 5; i++) log.Warning("No se pudo abrir el lector: {M}", "x");

        var enviado = reg.Pendientes(100);                                // viajan 5
        for (var i = 0; i < 3; i++) log.Warning("No se pudo abrir el lector: {M}", "x");   // llegan 3 más
        reg.Confirmar(enviado);

        var quedan = reg.Pendientes(100);
        Assert.Single(quedan);
        Assert.Equal(3, quedan[0].Repeticiones);   // sólo lo nuevo, para el próximo latido
    }

    [Fact]
    public void Sin_internet_mucho_tiempo_se_quedan_las_mas_nuevas_y_se_avisa()
    {
        var (log, reg) = Armar();
        var total = RegistroParaElServidor.Capacidad + 250;
        for (var i = 0; i < total; i++) log.Information("identify: match uid={Uid}", i);

        Assert.Equal(RegistroParaElServidor.Capacidad, reg.Cuantas);
        var p = reg.Pendientes(5000);
        Assert.Contains("se descartaron 250", p[0].Mensaje);
        Assert.Equal($"identify: match uid={total - 1}", p[^1].Mensaje);   // la última está

        reg.Confirmar(p);
        Assert.Equal(0, reg.Cuantas);
        Assert.Empty(reg.Pendientes(10));   // el aviso de descarte no se repite
    }
}

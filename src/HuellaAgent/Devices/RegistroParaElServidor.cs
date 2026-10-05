using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace HuellaAgent.Devices;

/// <summary>
/// Junta las líneas del registro que valen la pena para mandarlas en el latido.
///
/// POR QUÉ. Del 3 al 5 de octubre la PC de recepción rechazó a casi todos y desde afuera no
/// se podía saber por qué: el registro de verdad vive en %ProgramData%\HuellaAgent\logs y
/// para leerlo había que estar sentado en esa PC. Ahora viaja al servidor
/// (lector_registros, 48 h) con el latido de cada 5 minutos.
///
/// QUÉ SE MANDA:
///   · todo lo del agente de nivel Information para arriba (cada lectura con su puntaje y
///     contra cuántas huellas comparó, el portero, el relé, la config del gym);
///   · de .NET/ASP.NET (Microsoft.*, System.*) sólo avisos y errores: lo demás es ruido.
///
/// REPETIDOS. Un aviso o error IGUAL al anterior (misma plantilla) se suma al anterior en
/// vez de ocupar otra línea: "No se pudo abrir el lector" sale una vez por minuto cuando
/// está desenchufado, y serían 1.440 líneas por día diciendo lo mismo. Las lecturas NO se
/// agrupan: dos "match" seguidos son dos personas distintas.
///
/// SIN INTERNET. Se acumula hasta <see cref="Capacidad"/> líneas. Si se llena se tiran las
/// MÁS VIEJAS (lo reciente es lo que se investiga) y se anota cuántas se perdieron.
/// </summary>
public sealed class RegistroParaElServidor : ILogEventSink
{
    public const int Capacidad = 3000;

    public sealed class Linea
    {
        public DateTime EnUtc { get; set; }
        public string Nivel { get; init; } = "INF";
        public string Mensaje { get; set; } = "";
        public string Plantilla { get; init; } = "";
        public int Repeticiones { get; set; } = 1;
        public DateTime? HastaUtc { get; set; }
        /// <summary>La línea "se descartaron N" que agrega el propio registro: no está en la cola.</summary>
        public bool EsAvisoDeDescarte { get; init; }
    }

    // El mismo formato que el archivo ({Message:lj}): sin comillas alrededor de los textos,
    // para que la línea del servidor se lea igual que la de la PC.
    private static readonly MessageTemplateTextFormatter Formato = new("{Message:lj}");

    private readonly object _gate = new();
    private readonly LinkedList<Linea> _cola = new();
    private int _descartadas;

    public void Emit(LogEvent e)
    {
        var fuente = e.Properties.TryGetValue("SourceContext", out var sc) ? sc.ToString().Trim('"') : "";
        var deNet = fuente.StartsWith("Microsoft.", StringComparison.Ordinal) || fuente.StartsWith("System.", StringComparison.Ordinal);
        if (deNet && e.Level < LogEventLevel.Warning) return;
        if (e.Level < LogEventLevel.Information) return;

        var nivel = e.Level switch
        {
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error or LogEventLevel.Fatal => "ERR",
            _ => "INF",
        };
        var sw = new StringWriter();
        Formato.Format(e, sw);
        var mensaje = sw.ToString();
        if (e.Exception is not null) mensaje += " | " + e.Exception.GetType().Name + ": " + e.Exception.Message;
        var cuando = e.Timestamp.UtcDateTime;

        lock (_gate)
        {
            var ultima = _cola.Last?.Value;
            if (nivel != "INF" && ultima is not null && ultima.Nivel == nivel && ultima.Plantilla == e.MessageTemplate.Text)
            {
                ultima.Repeticiones++;
                ultima.HastaUtc = cuando;
                ultima.Mensaje = mensaje;   // el texto más reciente (p.ej. "540 intentos")
                return;
            }

            _cola.AddLast(new Linea { EnUtc = cuando, Nivel = nivel, Mensaje = mensaje, Plantilla = e.MessageTemplate.Text });
            while (_cola.Count > Capacidad)
            {
                _cola.RemoveFirst();
                _descartadas++;
            }
        }
    }

    /// <summary>
    /// Las primeras <paramref name="max"/> líneas pendientes, SIN sacarlas de la cola: se
    /// sacan con <see cref="Confirmar"/> recién cuando el servidor las tomó. Si el latido
    /// falla, se vuelven a mandar en el próximo.
    /// </summary>
    public IReadOnlyList<Linea> Pendientes(int max)
    {
        lock (_gate)
        {
            var lista = _cola.Take(max).Select(Copia).ToList();
            if (_descartadas > 0 && lista.Count > 0)
            {
                lista.Insert(0, new Linea
                {
                    EnUtc = lista[0].EnUtc.AddMilliseconds(-1),
                    Nivel = "WRN",
                    Mensaje = $"Registro: se descartaron {_descartadas} lineas viejas (estuvo mucho sin poder mandarlas)",
                    EsAvisoDeDescarte = true,
                });
            }
            return lista;
        }
    }

    /// <summary>
    /// El servidor tomó estas líneas: se sacan de la cola. Si mientras viajaban se le
    /// sumaron repetidos a la última, NO se saca: queda con sólo lo nuevo, para el próximo
    /// latido. Si no, esas repeticiones se perderían.
    /// </summary>
    public void Confirmar(IReadOnlyList<Linea> mandadas)
    {
        lock (_gate)
        {
            if (mandadas.Any(l => l.EsAvisoDeDescarte)) _descartadas = 0;
            foreach (var enviada in mandadas.Where(l => !l.EsAvisoDeDescarte))
            {
                var nodo = _cola.First;
                if (nodo is null) break;
                var viva = nodo.Value;
                if (viva.Repeticiones > enviada.Repeticiones)
                {
                    viva.Repeticiones -= enviada.Repeticiones;
                    viva.EnUtc = (enviada.HastaUtc ?? enviada.EnUtc).AddMilliseconds(1);
                    break;
                }
                _cola.RemoveFirst();
            }
        }
    }

    public int Cuantas { get { lock (_gate) return _cola.Count; } }

    // Copia: el lote que viaja no puede cambiar si mientras tanto se suma un repetido a la
    // última línea (eso se manda en el próximo latido, con el total actualizado).
    private static Linea Copia(Linea l) => new()
    {
        EnUtc = l.EnUtc, Nivel = l.Nivel, Mensaje = l.Mensaje, Plantilla = l.Plantilla,
        Repeticiones = l.Repeticiones, HastaUtc = l.HastaUtc,
    };
}

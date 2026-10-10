using System.Text;

namespace HuellaAgent.Qr;

/// <summary>
/// Arma los códigos con lo que llega por el puerto serie del lector QR.
///
/// En modo COM el lector manda el código como texto y, según cómo venga de fábrica, lo
/// cierra con CR, con LF, con los dos… o con nada. Y el puerto lo entrega en pedazos: un
/// mismo código puede llegar partido en dos lecturas. Por eso:
///  - CR o LF cierran el código (CR+LF no arma un segundo código vacío);
///  - sin cierre, <see cref="SilencioMs"/> sin bytes nuevos lo dan por terminado: el lector
///    manda todo el código de corrido, en unos pocos milisegundos;
///  - los caracteres de control se descartan (hay lectores que mandan STX/ETX alrededor).
///
/// A diferencia del modo teclado, acá no hay distribución de teclado de por medio: el guion
/// llega como guion aunque Windows esté en español.
/// </summary>
public sealed class CodigosDelLector
{
    public const int SilencioMs = 100;

    /// <summary>Más corto que esto no es un código: un byte suelto, ruido al enchufarlo.</summary>
    public const int Minimo = 4;

    /// <summary>Los nuestros tienen de 12 a 17; un QR de otra cosa no se acumula sin fin.</summary>
    public const int Maximo = 128;

    private readonly StringBuilder _buf = new();
    private DateTime _ultimoByte;

    /// <summary>Suma lo recién leído y devuelve los códigos que quedaron cerrados.</summary>
    public List<string> Agregar(byte[] datos, int cantidad, DateTime ahora)
    {
        var listos = new List<string>();
        for (var i = 0; i < cantidad; i++)
        {
            var b = datos[i];
            if (b == (byte)'\r' || b == (byte)'\n') { Cerrar(listos); continue; }
            if (b < 0x20 || b == 0x7F) continue;
            if (_buf.Length < Maximo) _buf.Append((char)b);
        }
        if (cantidad > 0) _ultimoByte = ahora;
        return listos;
    }

    /// <summary>El código que quedó sin cierre, si ya pasó el silencio. null = nada que cerrar.</summary>
    public string? Vencido(DateTime ahora)
    {
        if (_buf.Length == 0 || (ahora - _ultimoByte).TotalMilliseconds < SilencioMs) return null;
        var listos = new List<string>();
        Cerrar(listos);
        return listos.Count > 0 ? listos[0] : null;
    }

    private void Cerrar(List<string> listos)
    {
        var codigo = _buf.ToString().Trim();
        _buf.Clear();
        if (codigo.Length >= Minimo) listos.Add(codigo);
    }
}

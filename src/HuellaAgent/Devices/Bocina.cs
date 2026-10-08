using System.Runtime.InteropServices;
using HuellaAgent.Config;

namespace HuellaAgent.Devices;

/// <summary>
/// El aviso sonoro para la persona que apoyó el dedo, por los parlantes de la PC.
///
/// Hace falta porque en la puerta no hay pantalla: el socio apoya el dedo y se queda
/// mirando el torniquete sin saber si lo leyó o si tiene que insistir. Cuando abre, el
/// torniquete mismo avisa; **el caso que necesita sonido es el que NO abre**.
///
/// Se hace por software y no por el lector: probado el 18-sep con el SLK20R, los
/// parámetros de luz y zumbador (101-103) aceptan valores, devuelven ok y no hacen nada.
///
/// Hasta la v1.6 era `Console.Beep`, que no tiene volumen: suena siempre igual de fuerte.
/// Desde la v1.7 cada aviso es un WAV armado en memoria (ver <see cref="Tono"/>) y se toca
/// con PlaySound, para poder hacer el del equipo un 30 % más bajo. Las notas del socio y
/// del rechazo son las mismas de antes. Suena en la PC de recepción, así que sirve si
/// está cerca de la puerta; si no, hay que poner un zumbador en el relé.
/// </summary>
public sealed class Bocina
{
    /// <summary>Volumen de los avisos, de 0 a 1 de la escala completa.</summary>
    public const double Volumen = 0.8;

    /// <summary>
    /// El del equipo, un 30 % más bajo (pedido de Adriano, 07-oct): es la marca de un
    /// turno, no una entrada que haya que anunciar.
    /// </summary>
    public const double VolumenEquipo = Volumen * 0.7;

    // Se arman una sola vez: son unos pocos KB y no cambian.
    private static readonly byte[] SonidoOk = Tono.Wav(Volumen, (1200, 120));
    private static readonly byte[] SonidoRechazo = Tono.Wav(Volumen, (400, 180), (400, 180));
    private static readonly byte[] SonidoEquipo = Tono.Wav(VolumenEquipo, (784, 90), (784, 120));

    private readonly AgentConfig _cfg;
    private readonly ILogger<Bocina> _log;

    public Bocina(AgentConfig cfg, ILogger<Bocina> log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Pasa un socio: un pitido corto y agudo.</summary>
    public void Ok() => Sonar(SonidoOk);

    /// <summary>No pasa: dos pitidos graves, que no se confunden con el de arriba.</summary>
    public void Rechazo() => Sonar(SonidoRechazo);

    /// <summary>
    /// Pasa alguien del equipo: dos notas iguales y cortas (un "tin-tin"), más bajas.
    /// Recepción sabe quién entró sin mirar la pantalla. Es el mismo aviso que da el panel
    /// (useSound.playStaff), para que suene igual en todos los gimnasios.
    /// </summary>
    public void Equipo() => Sonar(SonidoEquipo);

    // Fire-and-forget: el sonido NO puede demorar la apertura de la puerta. PlaySound en
    // modo sincrónico bloquea el hilo mientras dura, así que va aparte.
    private void Sonar(byte[] wav)
    {
        if (!_cfg.SonidoEnabled || !OperatingSystem.IsWindows()) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_SYNC | SND_NODEFAULT))
                    _log.LogDebug("PlaySound no pudo emitir el aviso (error {Codigo})", Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                // PC sin salida de audio, o audio tomado por otra app: no es motivo para
                // que el portero deje de abrir la puerta.
                _log.LogDebug(ex, "No se pudo emitir el aviso sonoro");
            }
        });
    }

    private const uint SND_SYNC = 0x0000;
    private const uint SND_NODEFAULT = 0x0002;   // si falla, que no suene el "ding" de Windows
    private const uint SND_MEMORY = 0x0004;      // el primer parámetro es el WAV, no un archivo

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySound(byte[] sonido, IntPtr modulo, uint flags);
}

/// <summary>
/// Arma un WAV (PCM de 16 bits, mono, 44,1 kHz) con notas senoidales y un silencio corto
/// entre una y otra, a un volumen dado. Es lo que reemplaza a Console.Beep, que no tiene
/// volumen.
/// </summary>
public static class Tono
{
    public const int Muestreo = 44100;

    /// <summary>Silencio entre notas: el mismo que había entre los Console.Beep.</summary>
    public const int SilencioMs = 60;

    // Subida y bajada de cada nota. Sin esto el parlante hace "clic" al empezar y al
    // terminar, porque la onda arranca o se corta en seco.
    private const int RampaMs = 4;

    public static byte[] Wav(double volumen, params (int Frecuencia, int Ms)[] notas)
    {
        var muestras = new List<short>();
        for (var i = 0; i < notas.Length; i++)
        {
            if (i > 0) muestras.AddRange(new short[Muestras(SilencioMs)]);
            var (frecuencia, ms) = notas[i];
            var n = Muestras(ms);
            var rampa = Math.Min(Muestras(RampaMs), n / 2);
            for (var k = 0; k < n; k++)
            {
                var envolvente = Math.Min(1.0, Math.Min(k, n - 1 - k) / (double)Math.Max(1, rampa));
                var valor = volumen * envolvente * Math.Sin(2 * Math.PI * frecuencia * k / Muestreo);
                muestras.Add((short)Math.Round(valor * short.MaxValue));
            }
        }

        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        var datos = muestras.Count * 2;
        w.Write("RIFF"u8);
        w.Write(36 + datos);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                 // tamaño del bloque fmt
        w.Write((short)1);           // PCM
        w.Write((short)1);           // mono
        w.Write(Muestreo);
        w.Write(Muestreo * 2);       // bytes por segundo
        w.Write((short)2);           // bytes por muestra
        w.Write((short)16);          // bits por muestra
        w.Write("data"u8);
        w.Write(datos);
        foreach (var m in muestras) w.Write(m);
        w.Flush();
        return ms2.ToArray();
    }

    private static int Muestras(int ms) => Muestreo * ms / 1000;
}

using HuellaAgent.Devices;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// Los avisos dejaron de ser Console.Beep (sin volumen) y pasaron a ser WAV armados en
/// memoria, para que el del equipo suene un 30 % más bajo que el del socio (07-oct).
///
/// No se puede testear que "se oye": se testea que el WAV es uno que Windows sabe tocar
/// y que el volumen y la duración son los que se pidieron.
/// </summary>
public class BocinaTests
{
    private static short[] Muestras(byte[] wav)
    {
        var datos = BitConverter.ToInt32(wav, 40);
        var muestras = new short[datos / 2];
        Buffer.BlockCopy(wav, 44, muestras, 0, datos);
        return muestras;
    }

    private static int Pico(byte[] wav) => Muestras(wav).Max(m => Math.Abs((int)m));

    [Fact]
    public void Es_un_WAV_PCM_mono_de_16_bits()
    {
        var wav = Tono.Wav(Bocina.Volumen, (1200, 120));

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));          // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));          // mono
        Assert.Equal(Tono.Muestreo, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
    }

    [Fact]
    public void Dura_las_notas_mas_el_silencio_entre_ellas()
    {
        var wav = Tono.Wav(Bocina.VolumenEquipo, (784, 90), (784, 120));

        var esperadas = Tono.Muestreo * (90 + Tono.SilencioMs + 120) / 1000;
        Assert.Equal(esperadas, Muestras(wav).Length);
    }

    /// <summary>El pedido: el aviso del equipo, un 30 % más bajo que el del socio.</summary>
    [Fact]
    public void El_del_equipo_suena_un_30_por_ciento_mas_bajo()
    {
        var socio = Pico(Tono.Wav(Bocina.Volumen, (1200, 120)));
        var equipo = Pico(Tono.Wav(Bocina.VolumenEquipo, (784, 90), (784, 120)));

        Assert.InRange(equipo / (double)socio, 0.69, 0.71);
    }

    /// <summary>
    /// Sin rampa, la onda arranca y se corta en seco y el parlante hace "clic". La primera
    /// y la última muestra de cada nota tienen que estar en cero.
    /// </summary>
    [Fact]
    public void Cada_nota_empieza_y_termina_en_silencio()
    {
        var muestras = Muestras(Tono.Wav(Bocina.Volumen, (400, 180)));

        Assert.Equal(0, muestras[0]);
        Assert.Equal(0, muestras[^1]);
    }
}

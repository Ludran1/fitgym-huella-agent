using HuellaAgent.Devices;
using Xunit;

namespace HuellaAgent.Tests;

/// <summary>
/// La imagen del dedo que se le muestra a quien enrola.
///
/// Importa que sea un BMP de verdad y no "algo que parece": si el encabezado esta mal, el
/// navegador no muestra nada y el mostrador se queda sin la unica senal que tiene en la
/// PRIMERA captura — el puntaje de calidad ahi no existe, porque es una comparacion entre
/// dos capturas.
///
/// Y que salga DERECHA: con el alto positivo el BMP se lee de abajo hacia arriba y la
/// huella aparece dada vuelta. Nadie se daria cuenta mirando una huella (no tiene arriba
/// ni abajo evidente) y el centrado se juzgaria sobre una imagen espejada.
/// </summary>
public class ImagenDeLaHuellaTests
{
    /// Una franja clara arriba y oscura abajo: si se invierte, se nota.
    private static byte[] Degrade(int ancho, int alto)
    {
        var px = new byte[ancho * alto];
        for (int y = 0; y < alto; y++)
            for (int x = 0; x < ancho; x++)
                px[y * ancho + x] = (byte)(y < alto / 2 ? 240 : 16);
        return px;
    }

    [Fact]
    public void Es_un_bmp_de_8_bits_que_cualquier_navegador_entiende()
    {
        var bmp = Convert.FromBase64String(ImagenGris.ABmpBase64(Degrade(256, 360), 256, 360)!);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));          // tamano declarado
        Assert.Equal(40, BitConverter.ToInt32(bmp, 14));                 // BITMAPINFOHEADER
        Assert.Equal(256, BitConverter.ToInt32(bmp, 18));                // ancho
        Assert.Equal(8, BitConverter.ToInt16(bmp, 28));                  // bits por pixel
        Assert.Equal(0, BitConverter.ToInt32(bmp, 30));                  // sin compresion
    }

    [Fact]
    public void Sale_derecha_y_no_dada_vuelta()
    {
        var bmp = Convert.FromBase64String(ImagenGris.ABmpBase64(Degrade(256, 360), 256, 360)!);

        // Alto negativo = filas de arriba hacia abajo, como las entrega el SDK.
        Assert.Equal(-360, BitConverter.ToInt32(bmp, 22));

        // Y el primer pixel guardado es el de la franja CLARA, que es la de arriba.
        int inicio = BitConverter.ToInt32(bmp, 10);
        Assert.Equal(240, bmp[inicio]);
    }

    [Fact]
    public void Las_filas_quedan_alineadas_a_4_bytes()
    {
        // 250 no es multiplo de 4: cada fila lleva 2 bytes de relleno. Sin eso la imagen
        // sale inclinada, como una escalera, y pareceria que el dedo se movio.
        var bmp = Convert.FromBase64String(ImagenGris.ABmpBase64(Degrade(250, 10), 250, 10)!);
        int inicio = BitConverter.ToInt32(bmp, 10);
        Assert.Equal(252 * 10, bmp.Length - inicio);
    }

    [Fact]
    public void El_scanner_de_la_puerta_no_paga_el_costo_de_armar_la_imagen()
    {
        // `TryCapture()` la llama el scanner continuo cada pocos ms para identificar en la
        // puerta. Armar el BMP ahi serian ~90 KB mas su base64 (~215 KB) por cada dedo
        // leido, en bucle, en la PC de recepcion — y nadie mira esa foto: la imagen solo
        // existe para el enrolado, que entra por CaptureAsync.
        //
        // El default del parametro es lo unico que separa las dos cosas, asi que vale la
        // pena fijarlo: si alguien lo invierte, el costo vuelve y no se nota hasta que la
        // puerta de un gym grande se pone lenta.
        var metodo = typeof(HuellaAgent.Devices.IFingerprintDevice).GetMethod("TryCapture")!;
        var conImagen = metodo.GetParameters().Single();

        Assert.Equal("conImagen", conImagen.Name);
        Assert.Equal(false, conImagen.DefaultValue);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(400, 400)]   // 160.000 px no entran en un buffer de 256×360 = 92.160
    public void Si_las_medidas_no_cierran_devuelve_null_en_vez_de_una_imagen_corrida(int ancho, int alto)
    {
        // Mejor sin imagen que con una corrida: una imagen mal armada haria creer que el
        // dedo esta mal apoyado cuando no lo esta, y el socio repetiria sin motivo.
        Assert.Null(ImagenGris.ABmpBase64(new byte[256 * 360], ancho, alto));
    }
}

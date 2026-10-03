namespace HuellaAgent.Devices;

/// <summary>
/// La imagen del dedo, del buffer crudo del SDK a algo que un navegador pueda mostrar.
///
/// El SDK siempre la entrega: `AcquireFingerprint(device, imageBuffer, template, ref cb)`
/// llena `imageBuffer` con la huella en escala de grises, 1 byte por pixel, del ancho y
/// alto que informa el propio lector (parametros 1 y 2). Hasta la v1.2.10 ese buffer se
/// escribia y se tiraba: el agente solo devolvia el template.
///
/// Devolverla cambia el enrolado de raiz. El puntaje de calidad es una COMPARACION entre
/// dos capturas (`Match`), asi que en la primera no existe — no hay con que compararla. Y
/// la primera es justo donde se decide si el dedo esta centrado. Sin imagen, quien enrola
/// apoya a ciegas tres veces y recien al final se entera de que algo salio mal.
///
/// ── Por que BMP y no PNG ──────────────────────────────────────────────────────
/// PNG necesita deflate + CRC32 + adler32; BMP de 8 bits es un encabezado fijo mas los
/// pixeles. Son ~90 KB contra ~25 KB, pero esto viaja por LOCALHOST a la PC de al lado,
/// no por internet: no se gana nada comprimiendo y se agrega codigo que puede fallar.
/// Los navegadores muestran `data:image/bmp;base64,...` sin chistar.
/// </summary>
public static class ImagenGris
{
    /// <summary>
    /// BMP de 8 bits en escala de grises, en base64, listo para un `src="data:image/bmp"`.
    /// Devuelve null si las medidas no cierran con el buffer — mejor sin imagen que con
    /// una imagen corrida, que haria creer que el dedo esta mal apoyado cuando no lo esta.
    /// </summary>
    public static string? ABmpBase64(byte[] pixeles, int ancho, int alto)
    {
        if (ancho <= 0 || alto <= 0) return null;
        if (pixeles.Length < (long)ancho * alto) return null;

        // Cada fila del BMP se alinea a 4 bytes.
        int paso = (ancho + 3) / 4 * 4;
        int relleno = paso - ancho;

        const int tamEncabezado = 14 + 40;   // BITMAPFILEHEADER + BITMAPINFOHEADER
        const int tamPaleta = 256 * 4;       // gris: 256 entradas BGRA
        int tamPixeles = paso * alto;
        int total = tamEncabezado + tamPaleta + tamPixeles;

        var bmp = new byte[total];
        int i = 0;

        void U16(int v) { bmp[i++] = (byte)(v & 0xFF); bmp[i++] = (byte)((v >> 8) & 0xFF); }
        void U32(int v)
        {
            bmp[i++] = (byte)(v & 0xFF); bmp[i++] = (byte)((v >> 8) & 0xFF);
            bmp[i++] = (byte)((v >> 16) & 0xFF); bmp[i++] = (byte)((v >> 24) & 0xFF);
        }

        // BITMAPFILEHEADER
        bmp[i++] = (byte)'B'; bmp[i++] = (byte)'M';
        U32(total);
        U32(0);                                  // reservado
        U32(tamEncabezado + tamPaleta);          // donde arrancan los pixeles

        // BITMAPINFOHEADER
        U32(40);
        U32(ancho);
        // Alto NEGATIVO = las filas van de arriba hacia abajo, que es como las entrega el
        // SDK. Con alto positivo el BMP se lee de abajo hacia arriba y la huella sale dada
        // vuelta: el dedo se veria apuntando al reves y nadie sabria si eso esta bien.
        U32(-alto);
        U16(1);                                  // planos
        U16(8);                                  // bits por pixel
        U32(0);                                  // sin compresion
        U32(tamPixeles);
        U32(2835); U32(2835);                    // ~72 dpi, no lo usa nadie
        U32(256); U32(256);                      // colores de la paleta

        // Paleta: gris puro, BGRA.
        for (int g = 0; g < 256; g++)
        {
            bmp[i++] = (byte)g; bmp[i++] = (byte)g; bmp[i++] = (byte)g; bmp[i++] = 0;
        }

        // Pixeles, fila por fila, con el relleno al final de cada una.
        for (int y = 0; y < alto; y++)
        {
            Buffer.BlockCopy(pixeles, y * ancho, bmp, i, ancho);
            i += ancho;
            for (int r = 0; r < relleno; r++) bmp[i++] = 0;
        }

        return Convert.ToBase64String(bmp);
    }
}

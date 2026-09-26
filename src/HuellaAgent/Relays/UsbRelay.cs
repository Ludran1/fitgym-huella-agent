using System.IO.Ports;

namespace HuellaAgent.Relays;

/// <summary>
/// Modulo USB-rele de 1 canal por puerto serie (tipo JESSINIE / LCUS, chip CH340).
/// El agente abre el COM y manda los bytes de ON/OFF. Para los modulos LCUS los comandos
/// tipicos son: ON = A0 01 01 A2, OFF = A0 01 00 A1.
///
/// ⚠️ VERIFICAR contra el modulo real que compres: confirma que sea COM port (CH340) y los
/// bytes de comando en su descripcion/manual. Ajustar RelayOn/RelayOff si difieren.
/// </summary>
public sealed class UsbRelay : IRelay, IDisposable
{
    private static readonly byte[] RelayOn = { 0xA0, 0x01, 0x01, 0xA2 };
    private static readonly byte[] RelayOff = { 0xA0, 0x01, 0x00, 0xA1 };

    private readonly SerialPort _port;
    private readonly ILogger<UsbRelay> _log;

    /// <summary>
    /// Recibe el puerto YA RESUELTO y no la config entera: quien decide cual es lo hace
    /// antes (ver PuertoRele), porque puede venir del archivo o de la deteccion, y este
    /// objeto no tiene por que saber de cual de los dos.
    /// </summary>
    public UsbRelay(string port, ILogger<UsbRelay> log)
    {
        _log = log;
        _port = new SerialPort(port, 9600, Parity.None, 8, StopBits.One);

        // ⚠️ EL TIMEOUT NO ES OPCIONAL. `SerialPort.Write` es BLOQUEANTE y el default de
        // .NET es SerialPort.InfiniteTimeout. Si el adaptador CH340 queda en mal estado
        // —tipicamente porque Windows re-enumero el USB al enchufar o desenchufar otra
        // cosa— ese Write no vuelve NUNCA.
        //
        // Y el que lo llama es el hilo del portero. Asi que un relé colgado no deja al
        // torniquete sin abrir: deja al PORTERO MUERTO. Deja de mirar dedos, para siempre,
        // sin un solo error en el log, mientras /health sigue diciendo `turnstile: ready`
        // porque SerialPort.IsOpen tambien miente con el aparato desenchufado.
        //
        // Paso el 26-sep en recepcion al cambiar el SLK20R por el ZK9500: el lector nuevo
        // funcionaba perfecto (score 735 contra 645 del viejo), pero el desenchufe rompio
        // COM9 y el pulso siguiente se trago al portero. El sintoma que llego fue "cambie
        // el lector y no abre", y el lector no tenia nada que ver.
        //
        // Con el timeout puesto, el mismo caso tira TimeoutException -> ReconnectingRelay
        // cierra el puerto y lo reabre en el proximo pulso, redetectando el COM si hizo
        // falta. 2 s es muchisimo para escribir 4 bytes a 9600 baudios (son ~4 ms).
        _port.WriteTimeout = 2000;
        _port.ReadTimeout = 2000;

        _port.Open();
    }
    public bool IsConnected => _port.IsOpen;
    public string? LastError => _port.IsOpen ? null : "el puerto se cerro";
    public bool TryConnect() => _port.IsOpen;

    public async Task PulseAsync(int pulseMs, CancellationToken ct)
    {
        _port.Write(RelayOn, 0, RelayOn.Length);
        try { await Task.Delay(pulseMs, ct); }
        finally { _port.Write(RelayOff, 0, RelayOff.Length); }   // siempre reabrir el contacto
        _log.LogInformation("UsbRelay: pulso de {Ms} ms enviado a {Port}", pulseMs, _port.PortName);
    }

    public void Dispose()
    {
        if (_port.IsOpen)
        {
            try { _port.Write(RelayOff, 0, RelayOff.Length); } catch { /* best effort */ }
            _port.Close();
        }
        _port.Dispose();
    }
}

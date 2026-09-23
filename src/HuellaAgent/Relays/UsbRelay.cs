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

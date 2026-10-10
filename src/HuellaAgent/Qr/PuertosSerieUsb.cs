using System.Runtime.InteropServices;

namespace HuellaAgent.Qr;

/// <summary>Un puerto serie de un aparato USB enchufado, con el VID y el PID que dice Windows.</summary>
public sealed record PuertoUsb(string Puerto, string Vid, string Pid)
{
    public override string ToString() => $"{Puerto} {Vid}&{Pid}";
}

/// <summary>
/// Los puertos COM de los aparatos USB enchufados AHORA, y de qué aparato es cada uno.
///
/// Mismo camino que PuertoRele.PuertosDeCh340 (cfgmgr32 + `Device Parameters\PortName` en el
/// registro), pero de todos los aparatos y no sólo del CH340: con el lector QR en modo COM
/// hay dos puertos serie que le importan al agente, y cada uno tiene que saber cuál es el
/// suyo. Se leen sólo los presentes: el registro también guarda los puertos de los USB
/// donde algo estuvo enchufado antes.
/// </summary>
public static class PuertosSerieUsb
{
    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const int CR_SUCCESS = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int pulLen, string pszFilter, int ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] buffer, int bufferLen, int ulFlags);

    /// <summary>Vacía fuera de Windows o si no se puede preguntar.</summary>
    public static IReadOnlyList<PuertoUsb> Listar()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<PuertoUsb>();
        try
        {
            const int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out int len, "USB", flags) != CR_SUCCESS || len <= 1) return Array.Empty<PuertoUsb>();
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW("USB", buf, len, flags) != CR_SUCCESS) return Array.Empty<PuertoUsb>();
            var lista = new List<PuertoUsb>();
            foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters");
                if (k?.GetValue("PortName") is not string p || !p.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;
                var (vid, pid) = VidPid(id);
                if (vid is not null) lista.Add(new PuertoUsb(p.ToUpperInvariant(), vid, pid ?? "PID_????"));
            }
            return lista;
        }
        catch
        {
            return Array.Empty<PuertoUsb>();
        }
    }

    /// <summary>`USB\VID_9901&amp;PID_0301&amp;MI_00\…` → ("VID_9901", "PID_0301").</summary>
    public static (string? Vid, string? Pid) VidPid(string idDispositivo)
    {
        string? Tomar(string clave)
        {
            var i = idDispositivo.IndexOf(clave, StringComparison.OrdinalIgnoreCase);
            return i < 0 || i + clave.Length + 4 > idDispositivo.Length
                ? null
                : idDispositivo.Substring(i, clave.Length + 4).ToUpperInvariant();
        }
        return (Tomar("VID_"), Tomar("PID_"));
    }
}

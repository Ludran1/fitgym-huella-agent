#if ZKFP
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HuellaAgent.Devices;

/// <summary>
/// ¿Windows sigue viendo un lector ZKTeco enchufado?
///
/// Hace falta porque **el SDK no se entera del desenchufe**. Verificado el 18-sep con el
/// lector fuera del USB: `GetDeviceCount` seguia devolviendo 1 y `AcquireFingerprint`
/// seguia contestando -8 ("sin dedo"), asi que /health decia `connected` indefinidamente —
/// justo la mentira que la v1.1 vino a matar. El administrador de dispositivos de Windows
/// si lo sabe, y responder esa pregunta cuesta menos de un milisegundo.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UsbPresencia
{
    /// <summary>VID de ZKTeco: lo comparten SLK20R, ZK9500, ZK6500 y ZK8500R.</summary>
    public const string VidZkteco = "VID_1B55";

    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const int CR_SUCCESS = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out int pulLen, string pszFilter, int ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] buffer, int bufferLen, int ulFlags);

    /// <summary>
    /// true = hay al menos un dispositivo USB ZKTeco presente. Ante cualquier duda devuelve
    /// true: si no se puede preguntar, mejor no acusar al lector de estar desenchufado.
    /// </summary>
    public static bool HayLector(string vid = VidZkteco)
    {
        try
        {
            const int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out int len, "USB", flags) != CR_SUCCESS) return true;
            if (len <= 1) return false;   // no hay NINGUN USB presente

            var buf = new char[len];
            if (CM_Get_Device_ID_ListW("USB", buf, len, flags) != CR_SUCCESS) return true;
            return new string(buf).Contains(vid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
#endif

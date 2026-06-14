using System.Runtime.InteropServices;

namespace HuellaAgent.Devices;

/// <summary>
/// P/Invoke a la API NATIVA del ZKFinger SDK (libzkfp.dll). Compila en cualquier SO
/// (DllImport se resuelve en runtime); solo carga la DLL en Windows con el SDK presente.
///
/// ⚠️ VERIFICAR estas firmas contra el header del SDK que bajes (libzkfp.h / zkfperrdef.h):
/// los nombres y orden de parametros pueden variar segun la version del ZKFinger SDK.
/// Codigos: 0 = ZKFP_ERR_OK. Templates y la imagen viajan como byte[].
/// </summary>
internal static class ZkfpNative
{
    private const string Dll = "libzkfp";

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_Init();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_Terminate();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_GetDeviceCount();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr ZKFPM_OpenDevice(int index);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_CloseDevice(IntPtr handle);

    /// <summary>Captura imagen + template del dedo presente. Devuelve 0 si hay dedo.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_AcquireFingerprint(
        IntPtr handle,
        byte[] fpImage, uint cbFPImage,
        byte[] fpTemplate, ref uint cbTemplate);

    // ── DB en memoria (matching 1:N) ────────────────────────────────────────────
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr ZKFPM_DBInit();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_DBFree(IntPtr dbHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_DBClear(IntPtr dbHandle);

    /// <summary>Fusiona 3 templates de enrolado en 1 (regTemplate de salida).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_DBMerge(
        IntPtr dbHandle,
        byte[] temp1, byte[] temp2, byte[] temp3,
        byte[] regTemp, ref uint cbRegTemp);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_DBAdd(IntPtr dbHandle, uint fid, byte[] regTemp, uint cbRegTemp);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_DBDel(IntPtr dbHandle, uint fid);

    /// <summary>1:N contra la DB cargada. Devuelve fid + score del mejor match.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int ZKFPM_DBIdentify(
        IntPtr dbHandle, byte[] template, uint cbTemplate, ref uint fid, ref uint score);
}

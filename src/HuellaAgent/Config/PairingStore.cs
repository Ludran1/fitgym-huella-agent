using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HuellaAgent.Config;

/// <summary>Credenciales durables que el agente obtiene por pairing (1 clic desde la app).</summary>
/// <summary>
/// `Gym` se agrego en v1.1 y es opcional a proposito: un pairing.dat viejo (sin ese campo)
/// se sigue leyendo igual, queda con Gym=null y se completa solo al revincular.
/// </summary>
public sealed record Pairing(string Token, string SupabaseUrl, string AnonKey, string? TenantId, string? Gym = null);

/// <summary>
/// Persiste el pairing en disco, CIFRADO con DPAPI (LocalMachine) en Windows — así el token
/// del gym no queda en claro en la PC de recepción. En Linux/dev se guarda plano (sin DPAPI).
/// Tiene prioridad sobre appsettings: pairing = onboarding SaaS; appsettings = modo manual/dev.
/// </summary>
public sealed class PairingStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PairingStore(string path) => _path = path;

    public Pairing? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return null;
            try
            {
                var json = Decrypt(File.ReadAllBytes(_path));
                return JsonSerializer.Deserialize<Pairing>(json);
            }
            catch { return null; }   // archivo corrupto / no desencriptable → como no pareado
        }
    }

    public void Save(Pairing p)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(_path, Encrypt(JsonSerializer.Serialize(p)));
        }
    }

    public void Clear()
    {
        lock (_lock) { if (File.Exists(_path)) File.Delete(_path); }
    }

    private static byte[] Encrypt(string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
        return data;   // dev/Linux: sin DPAPI
    }

    private static string Decrypt(byte[] bytes)
    {
        if (OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine));
        return Encoding.UTF8.GetString(bytes);
    }
}

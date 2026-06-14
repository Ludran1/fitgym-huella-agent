namespace HuellaAgent.Storage;

/// <summary>Un template enrolado: liga un cliente (uuid) a su huella (base64) + uid del SDK.</summary>
public sealed class StoredTemplate
{
    public string ClienteId { get; set; } = "";
    public int Uid { get; set; }
    public string Template { get; set; } = "";
}

namespace HuellaAgent.Storage;

/// <summary>Un template enrolado: liga un cliente (uuid) a su huella (base64) + uid del SDK.</summary>
public sealed class StoredTemplate
{
    public string ClienteId { get; set; } = "";
    public int Uid { get; set; }
    public string Template { get; set; } = "";

    /// <summary>
    /// Que dedo de esa persona: 1 = principal, 2 = respaldo. Default 1.
    ///
    /// ZKTeco recomienda enrolar un dedo de CADA MANO, y nosotros guardabamos uno solo. Un
    /// socio con un corte, una curita o la mano seca en invierno no entra y no tiene
    /// alternativa — es lo que mas friccion genera en la puerta.
    ///
    /// El 1:N NO lo mira: resuelve por uid y dos uids del mismo cliente caen los dos en la
    /// misma persona. Existe para que `SaveAsync` no confunda un segundo dedo con un
    /// re-enrolado del primero y lo pise.
    ///
    /// Default 1 tambien al deserializar: un `templates.json` escrito por un agente
    /// anterior a la v1.4 no trae el campo, y esas huellas son todas el dedo principal.
    /// </summary>
    public int Dedo { get; set; } = 1;
}

namespace HuellaAgent.Relays;

/// <summary>
/// Rele de contacto seco para el torniquete (Fase 7, opcional por gym).
/// Setup decidido 2026-06-14: modulo USB-rele de 1 canal (entrada). La salida es un boton
/// mecanico cableado DIRECTO al torniquete → no pasa por aca. Por eso 1 solo canal.
/// </summary>
public interface IRelay
{
    bool IsConnected { get; }

    /// <summary>Cierra el contacto N.O. por <paramref name="pulseMs"/> y lo abre (pulso momentaneo).</summary>
    Task PulseAsync(int pulseMs, CancellationToken ct);
}

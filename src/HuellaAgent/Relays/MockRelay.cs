namespace HuellaAgent.Relays;

/// <summary>Rele simulado (Linux/CI/dev o torniquete deshabilitado): solo loguea el pulso.</summary>
public sealed class MockRelay : IRelay
{
    private readonly ILogger<MockRelay> _log;
    public MockRelay(ILogger<MockRelay> log) => _log = log;

    public bool IsConnected => true;
    public string? LastError => null;

    public Task PulseAsync(int pulseMs, CancellationToken ct)
    {
        _log.LogInformation("MockRelay: pulso de {Ms} ms (torniquete liberaria el giro)", pulseMs);
        return Task.CompletedTask;
    }
}

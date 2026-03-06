namespace Joselct.Communication.Contracts.Messages;

public abstract class IntegrationMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
    public string? Source { get; init; }
}
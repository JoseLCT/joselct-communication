namespace Joselct.Communication.Contracts.Messages;

public record RawMessage : IntegrationMessage
{
    public ReadOnlyMemory<byte> Body { get; init; }
    public string RoutingKey { get; init; } = "";
}
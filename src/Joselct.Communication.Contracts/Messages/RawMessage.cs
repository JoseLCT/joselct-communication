namespace Joselct.Communication.Contracts.Messages;

public class RawMessage : IntegrationMessage
{
    public ReadOnlyMemory<byte> Body { get; init; }
    public string RoutingKey { get; init; } = string.Empty;
}
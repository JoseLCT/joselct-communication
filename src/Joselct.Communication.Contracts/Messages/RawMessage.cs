namespace Joselct.Communication.Contracts.Messages;

public sealed record RawMessage : IntegrationMessage
{
    public byte[] Body { get; init; } = [];
    public string RoutingKey { get; init; } = string.Empty;
}
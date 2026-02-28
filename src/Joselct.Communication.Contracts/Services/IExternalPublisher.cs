using Joselct.Communication.Contracts.Messages;

namespace Joselct.Communication.Contracts.Services;

public interface IExternalPublisher
{
    Task PublishAsync<T>(
        T message,
        string? destination = null,
        bool declareDestination = false,
        CancellationToken ct = default
    ) where T : IntegrationMessage;
}
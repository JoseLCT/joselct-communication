using Joselct.Communication.Contracts.Messages;

namespace Joselct.Communication.Contracts.Services;

public interface IIntegrationMessageConsumer<T> where T : IntegrationMessage
{
    Task HandleAsync(T message, CancellationToken ct = default);
}
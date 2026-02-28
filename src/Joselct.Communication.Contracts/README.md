# Joselct.Communication.Contracts

Core contracts for the [Joselct.Communication](https://github.com/joselct/joselct-communication) library.

Reference this package when you need to share integration message types across multiple projects without pulling in the full RabbitMQ implementation.

## Installation

```bash
dotnet add package Joselct.Communication.Contracts
```

## What's Included

### `IntegrationMessage`

Base record for all messages exchanged between microservices:

```csharp
public abstract record IntegrationMessage
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? Source { get; init; }
}
```

Define your integration events by inheriting from it:

```csharp
public record OrderCreatedIntegrationEvent(
    Guid OrderId,
    string CustomerEmail,
    decimal Total
) : IntegrationMessage;
```

### `IExternalPublisher`

Publishes messages to the broker:

```csharp
public interface IExternalPublisher
{
    Task PublishAsync<T>(
        T message,
        string? destination = null,
        bool declareDestination = false,
        CancellationToken ct = default
    ) where T : IntegrationMessage;
}
```

### `IIntegrationMessageConsumer<T>`

Handles received messages:

```csharp
public interface IIntegrationMessageConsumer<T> where T : IntegrationMessage
{
    Task HandleAsync(T message, CancellationToken ct = default);
}
```

## Usage

### Defining integration events

```csharp
// Shared between microservices — only needs Contracts package
public record OrderCreatedIntegrationEvent(
    Guid OrderId,
    string CustomerEmail
) : IntegrationMessage;
```

### Implementing a consumer

```csharp
public class OrderCreatedConsumer : IIntegrationMessageConsumer<OrderCreatedIntegrationEvent>
{
    public async Task HandleAsync(OrderCreatedIntegrationEvent message, CancellationToken ct)
    {
        // process the event
    }
}
```

## Related Packages

- [Joselct.Communication.RabbitMQ](https://www.nuget.org/packages/Joselct.Communication.RabbitMQ) — RabbitMQ implementation

## License

MIT
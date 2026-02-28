# Joselct.Communication.RabbitMQ

RabbitMQ transport implementation for `Joselct.Communication.Contracts`.  
Supports .NET publishers and consumers with OpenTelemetry tracing, metrics, and dead letter queue handling.

## Quick start

### 1. Register in `Program.cs`

```csharp
builder.Services.AddRabbitMq(builder.Configuration);

// Register one consumer per queue
builder.Services.AddRabbitMqConsumer<OrderCreatedMessage, OrderCreatedHandler>(
    queueName: "orders.created",
    exchangeName: "orders-exchange",
    declareQueue: true);

// OpenTelemetry (optional)
builder.Services.AddOpenTelemetry()
    .AddRabbitMqInstrumentation();
```

### 2. Publish a message

```csharp
public class OrderService(IExternalPublisher publisher)
{
    public async Task CreateOrderAsync(Order order, CancellationToken ct)
    {
        // ... business logic ...

        await publisher.PublishAsync(new OrderCreatedMessage
        {
            OrderId = order.Id,
            Source = "orders-service"
        }, ct: ct);
    }
}
```

### 3. Handle a message

```csharp
public class OrderCreatedHandler : IIntegrationMessageConsumer<OrderCreatedMessage>
{
    public async Task HandleAsync(OrderCreatedMessage message, CancellationToken ct)
    {
        // Your business logic here
    }
}
```

## Configuration (`appsettings.json`)

```json
{
  "RabbitMQ": {
    "Host": "rabbitmq",
    "Port": 5672,
    "UserName": "guest",
    "Password": "guest",
    "VirtualHost": "/",
    "UseSsl": false,
    "ReconnectDelaySeconds": 5,
    "MaxReconnectAttempts": 0,
    "MaxRetryCount": 3,
    "DeadLetterExchange": "dlx"
  }
}
```

| Key                     | Default     | Description                              |
|-------------------------|-------------|------------------------------------------|
| `Host`                  | `localhost` | RabbitMQ hostname                        |
| `Port`                  | `5672`      | AMQP port (5671 for TLS)                 |
| `UserName`              | `guest`     | Username                                 |
| `Password`              | `guest`     | Password                                 |
| `VirtualHost`           | `/`         | Virtual host                             |
| `UseSsl`                | `false`     | Enable TLS                               |
| `ReconnectDelaySeconds` | `5`         | Delay between reconnection attempts      |
| `MaxReconnectAttempts`  | `0`         | `0` = unlimited                          |
| `MaxRetryCount`         | `3`         | Max processing retries before DLQ        |
| `DeadLetterExchange`    | `null`      | DLX name; if set, queues are bound to it |

## Retry & Dead Letter Queue

When a handler throws an unhandled exception:

1. The message is **requeued** up to `MaxRetryCount` times.
2. On the final failure, a `BasicNack` with `requeue: false` is sent.
3. If the queue has `x-dead-letter-exchange` configured (set via `DeadLetterExchange` option), RabbitMQ automatically
   routes the message to the DLQ.

The retry count is read from the standard `x-death` header that RabbitMQ populates automatically — no custom header
management is required.

**Recommended RabbitMQ setup:**

```
Exchange: dlx   (type: direct or fanout, durable: true)
Queue:    dlq   (bound to dlx)
```

## Interoperability

Tracing headers (`traceparent`, `tracestate`) are injected and read as **plain strings** (W3C TraceContext standard).
This ensures compatibility with consumers in Python, Go, Java, and other languages without any additional decoding.

The consumer also falls back to reading `byte[]` headers for backwards compatibility with older publisher versions.

## Metrics (OpenTelemetry)

| Metric                              | Description                     |
|-------------------------------------|---------------------------------|
| `rabbitmq.messages.published`       | Successfully published messages |
| `rabbitmq.messages.publish_failed`  | Failed publish attempts         |
| `rabbitmq.messages.bytes_published` | Total bytes published           |
| `rabbitmq.messages.received`        | Successfully processed messages |
| `rabbitmq.messages.consume_failed`  | Failed processing attempts      |
| `rabbitmq.messages.dead_lettered`   | Messages sent to DLQ            |
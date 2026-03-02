using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Joselct.Communication.Contracts.Messages;
using Joselct.Communication.Contracts.Services;
using Joselct.Communication.RabbitMQ.Config;
using Joselct.Communication.RabbitMQ.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Joselct.Communication.RabbitMQ.Services;

public class RabbitMqConsumer<T> : BackgroundService
    where T : IntegrationMessage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly ILogger<RabbitMqConsumer<T>> _logger;
    private readonly RabbitMqOptions _options;

    private readonly string _queueName;
    private readonly string? _exchangeName;
    private readonly string _routingKey;
    private readonly bool _declareQueue;

    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    public RabbitMqConsumer(
        IServiceScopeFactory scopeFactory,
        RabbitMqConnectionManager connectionManager,
        ILogger<RabbitMqConsumer<T>> logger,
        IOptions<RabbitMqOptions> options,
        string queueName,
        string? exchangeName = null,
        string routingKey = "",
        bool declareQueue = false)
    {
        _scopeFactory = scopeFactory;
        _connectionManager = connectionManager;
        _logger = logger;
        _options = options.Value;
        _queueName = queueName;
        _exchangeName = exchangeName;
        _routingKey = routingKey;
        _declareQueue = declareQueue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Consumer {Queue} crashed. Restarting in {Delay}s...",
                    _queueName, _options.ReconnectDelaySeconds);

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.ReconnectDelaySeconds),
                    stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var connection = await _connectionManager.GetConnectionAsync(ct);
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        if (_exchangeName is not null)
        {
            _logger.LogDebug("Verifying exchange {Exchange}", _exchangeName);
            await channel.ExchangeDeclarePassiveAsync(
                exchange: _exchangeName,
                cancellationToken: ct);
        }

        if (_declareQueue)
        {
            _logger.LogDebug("Declaring queue {Queue}", _queueName);

            var queueArgs = new Dictionary<string, object?>();

            if (!string.IsNullOrEmpty(_options.DeadLetterExchange))
            {
                queueArgs["x-dead-letter-exchange"] = _options.DeadLetterExchange;

                _logger.LogDebug(
                    "Queue {Queue} will use DLX {Dlx}",
                    _queueName, _options.DeadLetterExchange);
            }

            await channel.QueueDeclareAsync(
                queue: _queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: queueArgs.Count > 0 ? queueArgs : null,
                cancellationToken: ct);

            if (_exchangeName is not null)
            {
                await channel.QueueBindAsync(
                    queue: _queueName,
                    exchange: _exchangeName,
                    routingKey: _routingKey,
                    cancellationToken: ct
                );
            }
        }

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 1,
            global: false,
            cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleMessageAsync(channel, ea, ct);

        await channel.BasicConsumeAsync(
            queue: _queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: ct);

        _logger.LogInformation(
            "Consumer started on queue {Queue} (maxRetries={Max}, dlx={Dlx})",
            _queueName,
            _options.MaxRetryCount,
            _options.DeadLetterExchange ?? "none");

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task HandleMessageAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        CancellationToken ct)
    {
        var parentContext = Propagator.Extract(
            default,
            ea.BasicProperties,
            static (props, key) =>
            {
                if (props.Headers == null || !props.Headers.TryGetValue(key, out var value))
                    return [];

                return value switch
                {
                    string s => [s],
                    byte[] b => [Encoding.UTF8.GetString(b)],
                    _ => []
                };
            });

        Baggage.Current = parentContext.Baggage;

        using var activity = RabbitMqTelemetry.ActivitySource.StartActivity(
            $"{_queueName} consume",
            ActivityKind.Consumer,
            parentContext.ActivityContext);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination", _queueName);
        activity?.SetTag("messaging.destination_kind", "queue");
        activity?.SetTag("messaging.operation", "process");
        activity?.SetTag("messaging.message_payload_type", typeof(T).Name);

        T? message = null;

        try
        {
            message = JsonSerializer.Deserialize<T>(ea.Body.Span);

            if (message is null)
            {
                _logger.LogWarning(
                    "Received null message on queue {Queue}. Discarding.",
                    _queueName);
                await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
                return;
            }

            activity?.SetTag("messaging.message_id", message.Id.ToString());

            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IIntegrationMessageConsumer<T>>();

            await handler.HandleAsync(message, ct);

            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);

            RabbitMqTelemetry.MessagesReceived.Add(1,
                new KeyValuePair<string, object?>("queue", _queueName));

            _logger.LogInformation(
                "Processed message {Id} ({Type}) from {Queue}",
                message.Id, typeof(T).Name, _queueName);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to deserialize message from queue {Queue}. Discarding.",
                _queueName);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("messaging.error.type", "deserialization");

            RabbitMqTelemetry.ConsumeFailed.Add(1,
                new KeyValuePair<string, object?>("queue", _queueName));

            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
        }
        catch (Exception ex)
        {
            var retryCount = GetDeathCount(ea.BasicProperties);
            var shouldRequeue = retryCount < _options.MaxRetryCount;

            _logger.LogError(ex,
                "Error processing message {Id} from queue {Queue}. " +
                "Attempt {Attempt}/{Max}. {Action}.",
                message?.Id,
                _queueName,
                retryCount + 1,
                _options.MaxRetryCount,
                shouldRequeue ? "Requeueing" : "Sending to DLQ or discarding");

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("messaging.error.type", "processing");
            activity?.SetTag("messaging.retry_count", retryCount);

            if (!shouldRequeue)
            {
                RabbitMqTelemetry.MessagesDeadLettered.Add(1,
                    new KeyValuePair<string, object?>("queue", _queueName));
            }

            RabbitMqTelemetry.ConsumeFailed.Add(1,
                new KeyValuePair<string, object?>("queue", _queueName));

            await channel.BasicNackAsync(
                ea.DeliveryTag,
                multiple: false,
                requeue: shouldRequeue,
                cancellationToken: ct);
        }
    }

    private static int GetDeathCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers == null ||
            !properties.Headers.TryGetValue("x-death", out var xDeathRaw) ||
            xDeathRaw is not List<object> xDeathList ||
            xDeathList.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var entry in xDeathList)
        {
            if (entry is Dictionary<string, object> table &&
                table.TryGetValue("count", out var countRaw) &&
                countRaw is long count)
            {
                total += (int)count;
            }
        }

        return total;
    }
}
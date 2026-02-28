using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Joselct.Communication.Contracts.Messages;
using Joselct.Communication.Contracts.Services;
using Joselct.Communication.RabbitMQ.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

namespace Joselct.Communication.RabbitMQ.Services;

internal sealed class RabbitMqExternalPublisher : IExternalPublisher, IAsyncDisposable
{
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly ILogger<RabbitMqExternalPublisher> _logger;

    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private IChannel? _channel;
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    public RabbitMqExternalPublisher(
        RabbitMqConnectionManager connectionManager,
        ILogger<RabbitMqExternalPublisher> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task PublishAsync<T>(
        T message,
        string? destination = null,
        bool declareDestination = false,
        CancellationToken ct = default
    ) where T : IntegrationMessage
    {
        var exchangeName = destination ?? PascalToKebabCase(typeof(T).Name);

        using var activity = RabbitMqTelemetry.ActivitySource.StartActivity(
            $"{exchangeName} publish",
            ActivityKind.Producer,
            Activity.Current?.Context ?? default);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination", exchangeName);
        activity?.SetTag("messaging.destination_kind", "exchange");
        activity?.SetTag("messaging.message_payload_type", typeof(T).FullName);
        activity?.SetTag("messaging.message_id", message.Id.ToString());

        try
        {
            var channel = await GetOrCreateChannelAsync(ct);

            if (declareDestination)
            {
                _logger.LogDebug("Declaring exchange {Exchange}", exchangeName);
                await channel.ExchangeDeclareAsync(
                    exchange: exchangeName,
                    type: ExchangeType.Fanout,
                    durable: true,
                    cancellationToken: ct);
            }

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                MessageId = message.Id.ToString(),
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?>()
            };

            Propagator.Inject(
                new PropagationContext(activity?.Context ?? default, Baggage.Current),
                properties,
                static (props, key, value) => props.Headers![key] = value // string, not bytes
            );

            var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType());

            _logger.LogDebug(
                "Publishing {Type} to exchange {Exchange} ({Bytes} bytes)",
                typeof(T).Name, exchangeName, body.Length);

            await channel.BasicPublishAsync(
                exchange: exchangeName,
                routingKey: string.Empty,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: ct);

            RabbitMqTelemetry.MessagesPublished.Add(1,
                new KeyValuePair<string, object?>("exchange", exchangeName));
            RabbitMqTelemetry.BytesPublished.Add(body.Length,
                new KeyValuePair<string, object?>("exchange", exchangeName));

            _logger.LogInformation(
                "Published {Type} ({Id}) to {Exchange}",
                typeof(T).Name, message.Id, exchangeName);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            RabbitMqTelemetry.PublishFailed.Add(1,
                new KeyValuePair<string, object?>("exchange", exchangeName));

            _logger.LogError(ex,
                "Failed to publish {Type} to {Exchange}", typeof(T).Name, exchangeName);

            throw;
        }
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await _channelLock.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            var connection = await _connectionManager.GetConnectionAsync(ct);
            _channel = await connection.CreateChannelAsync(cancellationToken: ct);

            _logger.LogDebug("Created new RabbitMQ channel for publisher");
            return _channel;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync();
            _channel.Dispose();
        }

        _channelLock.Dispose();
    }

    private static string PascalToKebabCase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        return Regex.Replace(
                value,
                "(?<!^)([A-Z][a-z]|(?<=[a-z])[A-Z0-9])",
                "-$1",
                RegexOptions.Compiled)
            .Trim()
            .ToLower();
    }
}
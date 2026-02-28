using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Joselct.Communication.RabbitMQ.Telemetry;

internal static class RabbitMqTelemetry
{
    public const string ActivitySourceName = "Joselct.Communication.RabbitMQ";
    public const string MeterName = "Joselct.Communication.RabbitMQ";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> MessagesPublished =
        Meter.CreateCounter<long>("rabbitmq.messages.published",
            description: "Number of messages successfully published");

    public static readonly Counter<long> PublishFailed =
        Meter.CreateCounter<long>("rabbitmq.messages.publish_failed",
            description: "Number of messages that failed to publish");

    public static readonly Counter<long> BytesPublished =
        Meter.CreateCounter<long>("rabbitmq.messages.bytes_published",
            unit: "bytes",
            description: "Total bytes published");

    public static readonly Counter<long> MessagesReceived =
        Meter.CreateCounter<long>("rabbitmq.messages.received",
            description: "Number of messages successfully processed");

    public static readonly Counter<long> ConsumeFailed =
        Meter.CreateCounter<long>("rabbitmq.messages.consume_failed",
            description: "Number of messages that failed to process");

    public static readonly Counter<long> MessagesDeadLettered =
        Meter.CreateCounter<long>("rabbitmq.messages.dead_lettered",
            description: "Number of messages sent to DLQ after exhausting retries");
}
using System.Text.Json;
using System.Text.Json.Serialization;
using Joselct.Communication.Contracts.Messages;
using Joselct.Communication.Contracts.Services;
using Joselct.Communication.RabbitMQ.Config;
using Joselct.Communication.RabbitMQ.Services;
using Joselct.Communication.RabbitMQ.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Joselct.Communication.RabbitMQ.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRabbitMq(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(
            configuration.GetSection(RabbitMqOptions.SectionName));

        services.AddSingleton<RabbitMqConnectionManager>();

        services.AddScoped<IExternalPublisher, RabbitMqExternalPublisher>();

        services.Configure<JsonSerializerOptions>("rabbitmq",opts =>
        {
            opts.PropertyNameCaseInsensitive = true;
            opts.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        return services;
    }

    public static IServiceCollection AddRabbitMqConsumer<TMessage, THandler>(
        this IServiceCollection services,
        string queueName,
        string? exchangeName = null,
        string routingKey = "",
        bool declareQueue = false
    )
        where TMessage : IntegrationMessage
        where THandler : class, IIntegrationMessageConsumer<TMessage>
    {
        services.AddScoped<IIntegrationMessageConsumer<TMessage>, THandler>();

        services.AddHostedService(sp =>
            new RabbitMqConsumer<TMessage>(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<RabbitMqConnectionManager>(),
                sp.GetRequiredService<ILogger<RabbitMqConsumer<TMessage>>>(),
                sp.GetRequiredService<IOptions<RabbitMqOptions>>(),
                sp.GetRequiredService<IOptionsMonitor<JsonSerializerOptions>>().Get("rabbitmq"),
                queueName,
                exchangeName,
                routingKey,
                declareQueue
            )
        );

        return services;
    }

    public static OpenTelemetryBuilder AddRabbitMqInstrumentation(
        this OpenTelemetryBuilder builder)
    {
        return builder
            .WithTracing(tracing => tracing
                .AddSource(RabbitMqTelemetry.ActivitySourceName))
            .WithMetrics(metrics => metrics
                .AddMeter(RabbitMqTelemetry.MeterName));
    }
}
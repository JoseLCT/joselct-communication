using Joselct.Communication.RabbitMQ.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Joselct.Communication.RabbitMQ.Services;

public sealed class RabbitMqConnectionManager : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionManager> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private IConnection? _connection;
    private bool _disposed;

    public RabbitMqConnectionManager(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            _connection = await CreateConnectionWithRetryAsync(ct);
            return _connection;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<IConnection> CreateConnectionWithRetryAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            Ssl = _options.UseSsl
                ? new SslOption { Enabled = true, ServerName = _options.Host }
                : new SslOption { Enabled = false }
        };

        var attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;

            try
            {
                _logger.LogInformation(
                    "Connecting to RabbitMQ at {Host}:{Port} (attempt {Attempt})",
                    _options.Host, _options.Port, attempts);

                var connection = await factory.CreateConnectionAsync(ct);
                connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;

                _logger.LogInformation("Connected to RabbitMQ successfully");
                return connection;
            }
            catch (BrokerUnreachableException ex)
            {
                var maxAttempts = _options.MaxReconnectAttempts;

                if (maxAttempts > 0 && attempts >= maxAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Max reconnect attempts ({Max}) reached. Giving up.",
                        maxAttempts);
                    throw;
                }

                _logger.LogWarning(
                    ex,
                    "RabbitMQ unreachable. Retrying in {Delay}s (attempt {Attempt}/{Max})...",
                    _options.ReconnectDelaySeconds,
                    attempts,
                    maxAttempts == 0 ? "∞" : maxAttempts.ToString());

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), ct);
            }
        }
    }

    private async Task OnConnectionShutdownAsync(object? sender, ShutdownEventArgs e)
    {
        if (_disposed) return;

        _logger.LogWarning(
            "RabbitMQ connection lost: {Reason}. Will reconnect on next request.",
            e.ReplyText);

        await _semaphore.WaitAsync();
        try
        {
            _connection = null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_connection is not null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }

        _semaphore.Dispose();
    }
}
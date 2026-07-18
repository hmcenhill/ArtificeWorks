using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RabbitMQ.Client;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// Lazily establishes and shares one <see cref="IConnection"/>, declaring the
/// <c>artifice.events</c> exchange the first time the connection opens. A single lock
/// guards establishment so concurrent first-callers don't open competing connections.
/// Reconnection sophistication (retry/backoff) is deliberately deferred to Epic 8.
/// </summary>
public sealed class RabbitMqConnection : IRabbitMqConnection, IAsyncDisposable
{
    private readonly RabbitMqConfiguration _config;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnection(IOptions<RabbitMqConfiguration> config, ILogger<RabbitMqConnection> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        return await connection.CreateChannelAsync(cancellationToken: cancellationToken);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                HostName = _config.Host,
                Port = _config.Port,
                UserName = _config.Username,
                Password = _config.Password,
                VirtualHost = _config.VirtualHost,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            await DeclareTopologyAsync(_connection, cancellationToken);

            _logger.LogInformation(
                "Connected to RabbitMQ at {Host}:{Port}{VHost}; declared direct exchange {Exchange}",
                _config.Host, _config.Port, _config.VirtualHost, _config.ExchangeName);

            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Declares the shared direct exchange. Idempotent, so it is safe to run on every
    /// (re)connect. Consumer queues and bindings are declared by the Workers host (4.2).
    /// </summary>
    private async Task DeclareTopologyAsync(IConnection connection, CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            exchange: _config.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
        _connectionLock.Dispose();
    }
}

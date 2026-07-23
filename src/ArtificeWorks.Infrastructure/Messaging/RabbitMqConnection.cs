using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RabbitMQ.Client;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// Lazily establishes and shares one <see cref="IConnection"/>, declaring the
/// <c>artifice.events</c> exchange and 10.1's pace ladder the first time the connection opens. A
/// single lock guards establishment so concurrent first-callers don't open competing connections.
/// </summary>
public sealed class RabbitMqConnection : IRabbitMqConnection, IAsyncDisposable
{
    private readonly RabbitMqConfiguration _config;
    private readonly PaceConfiguration _pace;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnection(
        IOptions<RabbitMqConfiguration> config,
        IOptions<PaceConfiguration> pace,
        ILogger<RabbitMqConnection> logger)
    {
        _config = config.Value;
        _pace = pace.Value;
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
                "Connected to RabbitMQ at {Host}:{Port}{VHost}; declared direct exchange {Exchange} and pace ladder [{Ladder}]",
                _config.Host, _config.Port, _config.VirtualHost, _config.ExchangeName,
                string.Join(", ", Enumerable.Range(0, _pace.Rungs.Length).Select(_pace.LabelFor)));

            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Declares the shared direct exchange and 10.1's pace ladder. Idempotent, so it is safe to run
    /// on every (re)connect. Consumer queues, bindings and 8.2's retry ladder are declared by the
    /// Workers host (4.2, 8.2).
    /// <para>
    /// <strong>The pace ladder is declared here rather than with the retry ladder</strong>, and
    /// that asymmetry is deliberate: pacing is applied by <c>OutboxDispatcher</c>, which runs in
    /// all three hosts, and publishing to an exchange that does not exist closes the channel. So
    /// the ladder every publisher may use is declared by every publisher, on connect, next to the
    /// exchange it dead-letters into. See <see cref="BrokerTopology"/>.
    /// </para>
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

        await BrokerTopology.DeclarePaceLadderAsync(channel, _pace, _config.ExchangeName, cancellationToken);
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

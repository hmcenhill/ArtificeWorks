using RabbitMQ.Client;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// Owns the single shared RabbitMQ connection and hands out channels. Registered as a
/// singleton; the connection is established lazily on first use and the <c>artifice.events</c>
/// exchange is declared at that point. Channels are cheap and not thread-safe, so callers
/// take one per unit of work and dispose it.
/// </summary>
public interface IRabbitMqConnection
{
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
}

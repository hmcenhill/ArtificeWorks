namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// Broker connection + topology settings, bound from the <c>RabbitMqConfiguration</c>
/// configuration section. Lives in Infrastructure (moved from Api in 4.1) so both the
/// API and the Workers host bind the same shape.
/// </summary>
public class RabbitMqConfiguration
{
    public required string Host { get; set; }
    public required int Port { get; set; }
    public required string Username { get; set; }
    public required string Password { get; set; }
    public required string VirtualHost { get; set; }

    /// <summary>
    /// The single direct exchange every event is published to. Routing key = event type.
    /// </summary>
    public string ExchangeName { get; set; } = "artifice.events";
}

using RabbitMQ.Client;

namespace ArtificeWorks.Infrastructure.Messaging;

/// <summary>
/// Every exchange and queue this system declares, in one file — the two delay ladders included.
/// <para>
/// <strong>Both ladders have the same shape, and it is not a coincidence.</strong> A rung is a
/// fanout exchange with exactly one queue behind it; the queue holds a message for its
/// <c>x-message-ttl</c> and then dead-letters it back to <c>artifice.events</c>. Because neither
/// queue sets an <c>x-dead-letter-routing-key</c>, the expiring message keeps the routing key it
/// arrived with — the original event type — and re-enters the normal pipeline with no special-case
/// code in the consumer. That is also exactly why a rung is selected by <em>exchange</em> rather
/// than by routing key: the routing key is already spoken for.
/// </para>
/// <para>
/// <strong>They are declared in different places, deliberately.</strong> The retry ladder is a
/// consumer-side concern — nothing publishes to it but the consumer that failed — so
/// <c>RabbitMqConsumerService</c> declares it as part of coming up. The pace ladder is a
/// <em>publisher</em>-side concern: <c>OutboxDispatcher</c> runs in all three hosts and would close
/// its channel publishing to an exchange that did not exist, so the pace ladder is declared with
/// the shared exchange, on connect, by every host. Declaring both everywhere would be tidier and
/// would also mean the API declaring the queues only the worker consumes.
/// </para>
/// <para>
/// All of it is idempotent, so a fresh broker comes up complete and a restart changes nothing.
/// </para>
/// </summary>
public static class BrokerTopology
{
    /// <summary>
    /// The pace ladder (10.1): one fanout exchange and one TTL'd queue per rung, dead-lettering
    /// back into <paramref name="eventsExchange"/>.
    /// <para>
    /// Declared even when pacing is switched off. The rungs are empty queues costing nothing, and
    /// the alternative is that turning pacing on at runtime — which is the entire point of 10.2 —
    /// publishes into an exchange that does not exist yet.
    /// </para>
    /// </summary>
    public static async Task DeclarePaceLadderAsync(
        IChannel channel,
        PaceConfiguration pace,
        string eventsExchange,
        CancellationToken cancellationToken = default)
    {
        for (var rung = 0; rung < pace.Rungs.Length; rung++)
        {
            await DeclareDelayRungAsync(
                channel, pace.ExchangeFor(rung), pace.QueueFor(rung), pace.Rungs[rung],
                eventsExchange, cancellationToken);
        }
    }

    /// <summary>The retry ladder and the parked queue (8.2, 8.3).</summary>
    public static async Task DeclareRetryLadderAsync(
        IChannel channel,
        RetryConfiguration retry,
        string eventsExchange,
        CancellationToken cancellationToken = default)
    {
        for (var rung = 0; rung < retry.Delays.Length; rung++)
        {
            await DeclareDelayRungAsync(
                channel, retry.ExchangeFor(rung), retry.QueueFor(rung), retry.Delays[rung],
                eventsExchange, cancellationToken);
        }

        // No TTL, no dead-letter exchange, and no consumer of its own: a parked message stays
        // parked until 8.3's drain turns it into a row someone can look at.
        await channel.QueueDeclareAsync(
            queue: RetryConfiguration.ParkedQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);
    }

    private static async Task DeclareDelayRungAsync(
        IChannel channel,
        string exchange,
        string queue,
        int ttlMs,
        string deadLetterExchange,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: exchange, type: ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = ttlMs,
                ["x-dead-letter-exchange"] = deadLetterExchange,
                // Note the absence of x-dead-letter-routing-key. See the type's remarks: the
                // message must keep the routing key that says what it is.
            },
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(queue, exchange, routingKey: string.Empty, cancellationToken: cancellationToken);
    }
}

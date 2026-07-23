using System.Diagnostics;
using System.Text;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Infrastructure.Messaging;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ArtificeWorks.Workers.Consuming;

/// <summary>
/// The hosted consumer loop. Declares the durable work queue and 8.2's retry topology, binds the
/// queue to the shared <c>artifice.events</c> exchange for every handled event type, and
/// dispatches each delivery to the <see cref="EventDispatcher"/> with manual acks. Knows nothing
/// about concrete events — the set of routing keys comes from the registered handlers.
/// <para>
/// <strong>The failure taxonomy lives here, not in handlers</strong> (8.2). Handlers already
/// signal business outcomes by returning normally; anything that throws is this loop's problem to
/// categorise. Pushing the decision down would put retry policy in six places.
/// </para>
/// <list type="bullet">
///   <item><description><strong>business outcome</strong> → ack, unchanged since 4.2</description></item>
///   <item><description><strong>transient</strong> (DB blip, broker hiccup, 8.1's concurrency
///     conflict) → next rung of the delay ladder, ack the original</description></item>
///   <item><description><strong>poison</strong> (won't deserialize, no handler) → straight to
///     <c>artifice.parked</c>, no retries — replaying a parse failure is the same failure
///     again</description></item>
/// </list>
/// <para>
/// Every branch acks the delivery it was given. That is what makes a poison message unable to
/// wedge the queue: with prefetch 1, a message that threw and requeued would be redelivered
/// forever and nothing behind it would ever move.
/// </para>
/// </summary>
public sealed class RabbitMqConsumerService : BackgroundService
{
    /// <summary>The single durable queue this service consumes from.</summary>
    public const string QueueName = "artifice.workers";

    /// <summary>Readable attempt count carried alongside RabbitMQ's own <c>x-death</c> bookkeeping.</summary>
    public const string AttemptHeader = "x-attempt";

    private readonly IRabbitMqConnection _connection;
    private readonly EventDispatcher _dispatcher;
    private readonly RabbitMqRawPublisher _publisher;
    private readonly RabbitMqConfiguration _config;
    private readonly RetryConfiguration _retry;
    private readonly ArtificeWorksMetrics _metrics;
    private readonly ILogger<RabbitMqConsumerService> _logger;

    private IChannel? _channel;

    public RabbitMqConsumerService(
        IRabbitMqConnection connection,
        EventDispatcher dispatcher,
        RabbitMqRawPublisher publisher,
        IOptions<RabbitMqConfiguration> config,
        IOptions<RetryConfiguration> retry,
        ArtificeWorksMetrics metrics,
        ILogger<RabbitMqConsumerService> logger)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _publisher = publisher;
        _config = config.Value;
        _retry = retry.Value;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The shared connection declares the exchange on first use; we own the queues.
        _channel = await _connection.CreateChannelAsync(stoppingToken);

        await _channel.QueueDeclareAsync(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken);

        // 8.2's ladder and the parked queue. 10.1's pace ladder is declared on connect instead,
        // by every host — it is a publisher-side concern; see BrokerTopology.
        await BrokerTopology.DeclareRetryLadderAsync(_channel, _retry, _config.ExchangeName, stoppingToken);

        // Bind only the routing keys we actually handle. A new handler adds its key here
        // automatically via the dispatcher — no change to this loop.
        foreach (var eventType in _dispatcher.HandledEventTypes)
        {
            await _channel.QueueBindAsync(
                queue: QueueName,
                exchange: _config.ExchangeName,
                routingKey: eventType,
                cancellationToken: stoppingToken);
        }

        // One unacknowledged message at a time — simple and fair for the current single-consumer
        // slice, and the reason a retry must never be an in-process sleep.
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Worker consuming queue {Queue}, bound to [{EventTypes}] on exchange {Exchange}; retry ladder [{Ladder}] then {Parked}.",
            QueueName, string.Join(", ", _dispatcher.HandledEventTypes), _config.ExchangeName,
            string.Join(", ", Enumerable.Range(0, _retry.Delays.Length).Select(_retry.LabelFor)),
            RetryConfiguration.ParkedQueueName);

        // The consumer runs on its own callbacks; hold the service open until shutdown.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful shutdown.
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        // The publisher routes by event type, so the routing key IS the event type.
        var eventType = eventArgs.RoutingKey;
        var attempt = ReadAttempt(eventArgs.BasicProperties);

        // The other half of 9.1's AMQP hop. Extracted with the standard propagator, so the
        // consumer span is a child of the producer span the outbox dispatcher opened — and a
        // message that climbed a rung of the retry ladder rejoins the ORIGINAL trace rather than
        // starting a third one, because the ladder republishes from inside this very span.
        var incoming = Propagators.DefaultTextMapPropagator.Extract(
            default, eventArgs.BasicProperties.Headers, ReadHeader);

        Baggage.Current = incoming.Baggage;

        using var activity = ArtificeWorksTelemetry.ActivitySource.StartActivity(
            $"process {eventType}", ActivityKind.Consumer, incoming.ActivityContext);

        activity?.SetTag(ArtificeWorksTelemetry.MessagingSystem, ArtificeWorksTelemetry.RabbitMq);
        activity?.SetTag(ArtificeWorksTelemetry.MessagingOperation, "process");
        activity?.SetTag(ArtificeWorksTelemetry.MessagingDestination, QueueName);
        activity?.SetTag(ArtificeWorksTelemetry.MessagingMessageId, eventArgs.BasicProperties.MessageId);
        activity?.SetTag(ArtificeWorksTelemetry.EventTypeAttribute, eventType);
        activity?.SetTag(ArtificeWorksTelemetry.AttemptAttribute, attempt);
        activity?.SetTag(ArtificeWorksTelemetry.CorrelationIdAttribute, eventArgs.BasicProperties.CorrelationId);

        var started = Stopwatch.GetTimestamp();
        var outcome = "acked";

        // The publisher stamps the envelope's correlation id onto the AMQP correlation_id
        // property, so we open the log scope from message metadata alone — no need to
        // deserialize the body first. The thread survives every hop of the ladder because the
        // properties are copied forward on each republish.
        using (_logger.BeginScope("EventType:{EventType} EventId:{EventId} Attempt:{Attempt}",
                   eventType, eventArgs.BasicProperties.MessageId ?? "?", attempt))
        using (_logger.BeginCorrelationScope(eventArgs.BasicProperties.CorrelationId))
        {
            try
            {
                await _dispatcher.DispatchAsync(eventType, eventArgs.Body, eventArgs.CancellationToken);
                await _channel!.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, eventArgs.CancellationToken);
            }
            catch (PoisonMessageException e)
            {
                outcome = "parked";
                activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                await ParkAsync(eventArgs, eventType, attempt, e, "the message is permanently unhandleable");
            }
            catch (Exception e)
            {
                outcome = attempt > _retry.MaxAttempts ? "parked" : "retried";
                activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                await RetryOrParkAsync(eventArgs, eventType, attempt, e);
            }

            _metrics.MessageHandled(eventType, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Reads a propagation header off an AMQP delivery. The client hands string headers back as
    /// <c>byte[]</c>, which is why this cannot just be a dictionary lookup.
    /// </summary>
    private static IEnumerable<string> ReadHeader(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return [value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value.ToString() ?? string.Empty];
    }

    /// <summary>
    /// A transient failure. Push the message onto the next rung and ack the original — the delay
    /// is the rung's TTL, and the message is durable in a real queue while it waits, so a worker
    /// restart doesn't lose the retry.
    /// </summary>
    private async Task RetryOrParkAsync(BasicDeliverEventArgs eventArgs, string eventType, int attempt, Exception failure)
    {
        if (attempt > _retry.MaxAttempts)
        {
            await ParkAsync(eventArgs, eventType, attempt, failure,
                $"the retry ladder is exhausted after {_retry.MaxAttempts} retries");
            return;
        }

        var rung = attempt - 1;

        try
        {
            await _publisher.PublishToAsync(
                exchange: _retry.ExchangeFor(rung),
                routingKey: eventType,
                payload: Encoding.UTF8.GetString(eventArgs.Body.Span),
                eventId: ParseGuid(eventArgs.BasicProperties.MessageId),
                correlationId: ParseGuid(eventArgs.BasicProperties.CorrelationId),
                headers: HeadersWithAttempt(eventArgs.BasicProperties, attempt + 1),
                cancellationToken: eventArgs.CancellationToken);

            _metrics.MessageRetried(eventType, _retry.LabelFor(rung));

            // Warning, not Information (9.3's levelling pass): a retry is the pipeline visibly
            // recovering, and it is one of the lines Epic 12's audience is watching for. The
            // exception goes in the exception parameter so a backend can index it, rather than
            // being pasted into the message.
            _logger.LogWarning(failure,
                "Handling {EventType} failed on attempt {Attempt}; retrying in {Delay} via {Exchange}.",
                eventType, attempt, _retry.LabelFor(rung), _retry.ExchangeFor(rung));

            await _channel!.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, eventArgs.CancellationToken);
        }
        catch (Exception republishFailure)
        {
            // We could not hand the message to the ladder. Requeue it rather than lose it: the
            // broker is evidently unwell, and this is the one place where holding the message is
            // better than dropping it.
            _logger.LogError(republishFailure,
                "Could not schedule a retry for {EventType} (attempt {Attempt}); requeuing.", eventType, attempt);

            await _channel!.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true, eventArgs.CancellationToken);
        }
    }

    /// <summary>
    /// The end of the line: onto <c>artifice.parked</c> and acked. Logged at warning because this
    /// is precisely the line Epic 12's audience will be watching for.
    /// </summary>
    private async Task ParkAsync(
        BasicDeliverEventArgs eventArgs, string eventType, int attempt, Exception failure, string why)
    {
        _metrics.MessageParked(eventType);

        _logger.LogWarning(failure,
            "Parking {EventType} after {Attempt} attempt(s): {Why}. It is now a dead letter awaiting a human.",
            eventType, attempt, why);

        try
        {
            // The default exchange routes by queue name, so no parked exchange is needed. The
            // original routing key travels as a header instead — 8.3 replays under it.
            var headers = HeadersWithAttempt(eventArgs.BasicProperties, attempt);
            headers["x-original-routing-key"] = eventType;
            headers["x-death-reason"] = $"{why}: {failure.Message}";

            await _publisher.PublishToAsync(
                exchange: string.Empty,
                routingKey: RetryConfiguration.ParkedQueueName,
                payload: Encoding.UTF8.GetString(eventArgs.Body.Span),
                eventId: ParseGuid(eventArgs.BasicProperties.MessageId),
                correlationId: ParseGuid(eventArgs.BasicProperties.CorrelationId),
                headers: headers,
                cancellationToken: eventArgs.CancellationToken);

            await _channel!.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, eventArgs.CancellationToken);
        }
        catch (Exception parkFailure)
        {
            _logger.LogError(parkFailure,
                "Could not park {EventType}; requeuing so it is not lost.", eventType);

            await _channel!.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true, eventArgs.CancellationToken);
        }
    }

    /// <summary>
    /// Which delivery this is, 1-based. Read from our own header rather than parsing RabbitMQ's
    /// <c>x-death</c> table: the death count is authoritative for a single dead-lettering path,
    /// but a message that crosses three different delay queues accumulates three separate
    /// entries, and "add up the counts of the queues I happen to know about" is a worse
    /// contract than one integer we control.
    /// </summary>
    private static int ReadAttempt(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is not null
            && properties.Headers.TryGetValue(AttemptHeader, out var raw)
            && raw is not null)
        {
            return raw switch
            {
                int value => value,
                long value => (int)value,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                _ => 1
            };
        }

        return 1;
    }

    private static Dictionary<string, object?> HeadersWithAttempt(IReadOnlyBasicProperties properties, int attempt)
    {
        var headers = properties.Headers is null
            ? []
            : new Dictionary<string, object?>(properties.Headers);

        headers[AttemptHeader] = attempt;
        return headers;
    }

    /// <summary>
    /// A message that reached us without a parseable id is odd but not fatal; the ladder should
    /// still work for it, so fall back to a fresh one rather than throwing on the failure path.
    /// </summary>
    private static Guid ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : Guid.NewGuid();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken);
            await _channel.DisposeAsync();
            _channel = null;
        }
    }
}

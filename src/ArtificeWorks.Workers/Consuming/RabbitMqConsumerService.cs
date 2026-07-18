using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Infrastructure.Messaging;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ArtificeWorks.Workers.Consuming;

/// <summary>
/// The hosted consumer loop. Declares a durable work queue, binds it to the shared
/// <c>artifice.events</c> exchange for every handled event type, and dispatches each
/// delivery to the <see cref="EventDispatcher"/> with manual acks. Knows nothing about
/// concrete events — the set of routing keys comes from the registered handlers.
/// </summary>
public sealed class RabbitMqConsumerService : BackgroundService
{
    /// <summary>The single durable queue this service consumes from.</summary>
    public const string QueueName = "artifice.workers";

    private readonly IRabbitMqConnection _connection;
    private readonly EventDispatcher _dispatcher;
    private readonly RabbitMqConfiguration _config;
    private readonly ILogger<RabbitMqConsumerService> _logger;

    private IChannel? _channel;

    public RabbitMqConsumerService(
        IRabbitMqConnection connection,
        EventDispatcher dispatcher,
        IOptions<RabbitMqConfiguration> config,
        ILogger<RabbitMqConsumerService> logger)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The shared connection declares the exchange on first use; we own the queue.
        _channel = await _connection.CreateChannelAsync(stoppingToken);

        await _channel.QueueDeclareAsync(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken);

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

        // One unacknowledged message at a time — simple and fair for the first slice.
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;

        await _channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Worker consuming queue {Queue}, bound to [{EventTypes}] on exchange {Exchange}.",
            QueueName, string.Join(", ", _dispatcher.HandledEventTypes), _config.ExchangeName);

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

        // The publisher stamps the envelope's correlation id onto the AMQP correlation_id
        // property, so we open the log scope from message metadata alone — no need to
        // deserialize the body first. Every log line for this delivery (the dispatcher's,
        // the handler's, the nack below) inherits the id via the shared correlation scope,
        // matching the API's request scope on the other side (4.3). The delivery scope adds
        // the event type and id for local triage.
        using (_logger.BeginScope("EventType:{EventType} EventId:{EventId}", eventType, eventArgs.BasicProperties.MessageId ?? "?"))
        using (_logger.BeginCorrelationScope(eventArgs.BasicProperties.CorrelationId))
        {
            try
            {
                await _dispatcher.DispatchAsync(eventType, eventArgs.Body, eventArgs.CancellationToken);
                await _channel!.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, eventArgs.CancellationToken);
            }
            catch (Exception ex)
            {
                // Nack WITHOUT requeue: with no dead-letter queue yet (Epic 8), requeuing a
                // poison message would loop forever, so we drop it — the deliberate first-slice
                // policy. Catching here also guarantees a faulty handler can't kill the loop.
                _logger.LogError(ex,
                    "Handling {EventType} (delivery {DeliveryTag}) failed; nacking without requeue — message dropped.",
                    eventType, eventArgs.DeliveryTag);

                await _channel!.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: false, eventArgs.CancellationToken);
            }
        }
    }

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

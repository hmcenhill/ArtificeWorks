# Messaging topology

How ArtificeWorks moves events from the API to the workers over RabbitMQ, and how a
single correlation id threads one work order's story through both services' logs.

This document is the source of truth for the broker layout. You should be able to draw the
exchanges, queues, and bindings from it without reading code.

## At a glance

```mermaid
flowchart LR
    api["ArtificeWorks.Api<br/>(publisher)"]
    ex{{"exchange<br/><b>artifice.events</b><br/>type: direct, durable"}}
    q[["queue<br/><b>artifice.workers</b><br/>durable"]]
    worker["ArtificeWorks.Workers<br/>(consumer)"]

    api -- "publish<br/>routing key = event type" --> ex
    ex -- "binding: work-order.scheduled" --> q
    q -- "deliver (prefetch 1, manual ack)" --> worker
```

## Exchange

| Property | Value |
| --- | --- |
| Name | `artifice.events` |
| Type | `direct` |
| Durable | yes |
| Auto-delete | no |

There is **one** exchange for the whole system. Every event is published to it with the
**event type as the routing key** (e.g. `work-order.scheduled`). A direct exchange routes a
message only to queues bound with a routing key equal to the message's — so a queue opts in
to exactly the event types it names, and nothing else.

Why direct (not topic or fanout): routing keys here are exact, flat event-type strings with
no wildcard subscriptions, and not every consumer should see every event. Direct is the
simplest exchange that gives per-event-type subscription. If a future consumer needs
pattern subscriptions (`work-order.*`), that's the point to revisit topic.

The exchange is declared by the shared connection on first use
([`RabbitMqConnection`](../src/ArtificeWorks.Infrastructure/Messaging/RabbitMqConnection.cs)),
so whichever service starts first declares it; the declaration is idempotent.

## Queues and bindings

| Queue | Durable | Bound routing keys | Consumer |
| --- | --- | --- | --- |
| `artifice.workers` | yes | one per handled event type — currently `work-order.scheduled` | `ArtificeWorks.Workers` |

The worker owns a single durable queue. On startup it declares the queue and then binds it
to `artifice.events` **once per handled event type** — the set of bindings is derived from
the registered handlers, not hard-coded. Registering a new handler
(`AddEventHandler<TEvent, THandler>()`) adds its event type to that set, so the binding
appears automatically with no change to the consumer loop.

Only bound routing keys reach the queue. An event type with no handler is never delivered
(the direct exchange drops it for this queue), which is why the queue's bindings and the
worker's handler set are always the same list.

### Delivery and acknowledgement

- **Prefetch 1** — the worker holds at most one unacknowledged message at a time. Simple and
  fair for the current single-consumer slice.
- **Manual acks** — a message is acked only after its handler succeeds.
- **Nack without requeue on failure** — if a handler throws, the message is nacked with
  `requeue: false` and dropped. There is no dead-letter queue yet, so requeuing a poison
  message would loop forever. Epic 8 (reliability) adds a DLQ and revisits this policy.

## Message shape

Each message body is a JSON [`EventEnvelope<T>`](../src/ArtificeWorks.Application/Messaging/EventEnvelope.cs)
(camelCase, web defaults) wrapping the typed event payload. Alongside the body, these AMQP
basic properties are set by the publisher so a consumer or the management UI can triage
without deserializing:

| AMQP property | Value | Source |
| --- | --- | --- |
| `type` | event type (also the routing key) | `envelope.EventType` |
| `message_id` | unique id for this message | `envelope.EventId` |
| `correlation_id` | ties the message to one logical operation | `envelope.CorrelationId` |
| `content_type` | `application/json` | fixed |
| `delivery_mode` | persistent (2) | fixed — survives a broker restart on a durable queue |
| `timestamp` | publish time (unix seconds) | publisher clock |

## Correlation

A correlation id is the thread that ties one work order's whole story together across both
services. It flows in one direction, established once at the edge:

```mermaid
flowchart LR
    req["HTTP request<br/>(X-Correlation-ID header,<br/>else generated)"]
    subgraph API
      mw["CorrelationMiddleware<br/>sets CorrelationContext<br/>+ opens log scope"]
      pub["publisher stamps<br/>envelope.CorrelationId<br/>+ AMQP correlation_id"]
    end
    subgraph Worker
      con["consumer reads AMQP<br/>correlation_id → opens<br/>log scope"]
      h["handler logs"]
    end
    req --> mw --> pub -->|"message"| con --> h
```

1. **Established at the API boundary.** `CorrelationMiddleware` honours an inbound
   `X-Correlation-ID` request header when it's a valid Guid, otherwise uses the fresh id the
   per-request `CorrelationContext` defaults to. The id is echoed back on the response's
   `X-Correlation-ID` header.
2. **Carried on the event.** The publisher stamps that id onto both `envelope.CorrelationId`
   and the AMQP `correlation_id` property of every event raised during the request.
3. **Resumed in the worker.** On each delivery the consumer reads the AMQP `correlation_id`
   and opens a logging scope with it — no need to deserialize the body first.
4. **In the logs on both sides.** Both services push the id into a logging scope under the
   same key (`CorrelationId`, see
   [`CorrelationLog`](../src/ArtificeWorks.Application/Messaging/CorrelationLog.cs)) with
   console scopes enabled, so **one `grep` of a correlation id returns every log line — API
   and worker — for that operation.**

The id currently propagates API → event → worker. When workers begin re-publishing events
(Epic 5+), they should carry the consumed id forward onto anything they emit, so the thread
extends across multi-hop flows. That's future work; today the worker is a leaf consumer.

## Related code

- Exchange + connection: [`RabbitMqConnection`](../src/ArtificeWorks.Infrastructure/Messaging/RabbitMqConnection.cs), [`RabbitMqConfiguration`](../src/ArtificeWorks.Infrastructure/Messaging/RabbitMqConfiguration.cs)
- Publisher: [`RabbitMqEventPublisher`](../src/ArtificeWorks.Infrastructure/Messaging/RabbitMqEventPublisher.cs)
- Consumer + queue/bindings: [`RabbitMqConsumerService`](../src/ArtificeWorks.Workers/Consuming/RabbitMqConsumerService.cs), [`EventDispatcher`](../src/ArtificeWorks.Workers/Consuming/EventDispatcher.cs)
- Correlation: [`CorrelationMiddleware`](../src/ArtificeWorks.Api/Middleware/CorrelationMiddleware.cs), [`CorrelationContext`](../src/ArtificeWorks.Application/Messaging/CorrelationContext.cs), [`CorrelationLog`](../src/ArtificeWorks.Application/Messaging/CorrelationLog.cs)

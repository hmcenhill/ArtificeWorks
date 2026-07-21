using System.Text.Json;

using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;

namespace ArtificeWorks.UnitTests;

/// <summary>
/// Contract tests for the event envelope + payloads. These lock the wire shape the
/// worker (4.2) and the dashboard feed (Epic 11) will deserialize, so a round-trip must
/// preserve every field and the self-describing metadata must match the payload.
/// </summary>
public class EventContractTests
{
    // Matches the publisher's serializer settings (Infrastructure RabbitMqEventPublisher).
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void WorkOrderScheduled_envelope_round_trips_cleanly()
    {
        var payload = new WorkOrderScheduled(
            WorkOrderId: Guid.NewGuid(),
            ProductId: "CUSTODIAN-STD",
            ProductName: "Custodian",
            Quantity: 3,
            ScheduledUtc: new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc));

        var envelope = new EventEnvelope<WorkOrderScheduled>(
            EventId: Guid.NewGuid(),
            EventType: payload.EventType,
            SchemaVersion: payload.SchemaVersion,
            CorrelationId: Guid.NewGuid(),
            OccurredUtc: DateTime.UtcNow,
            Payload: payload);

        var json = JsonSerializer.Serialize(envelope, Options);
        var restored = JsonSerializer.Deserialize<EventEnvelope<WorkOrderScheduled>>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(envelope.EventId, restored!.EventId);
        Assert.Equal(envelope.EventType, restored.EventType);
        Assert.Equal(envelope.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(envelope.CorrelationId, restored.CorrelationId);
        Assert.Equal(envelope.OccurredUtc, restored.OccurredUtc);
        Assert.Equal(payload, restored.Payload); // record value equality across all fields
    }

    [Fact]
    public void WorkOrderCreated_envelope_round_trips_cleanly()
    {
        var payload = new WorkOrderCreated(
            WorkOrderId: Guid.NewGuid(),
            ProductId: "DELVER-MINE",
            ProductName: "Delver",
            Quantity: 1,
            Requestor: "ops@hermannsson",
            CreatedUtc: new DateTime(2026, 7, 18, 9, 30, 0, DateTimeKind.Utc));

        var envelope = new EventEnvelope<WorkOrderCreated>(
            Guid.NewGuid(), payload.EventType, payload.SchemaVersion,
            Guid.NewGuid(), DateTime.UtcNow, payload);

        var json = JsonSerializer.Serialize(envelope, Options);
        var restored = JsonSerializer.Deserialize<EventEnvelope<WorkOrderCreated>>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(payload, restored!.Payload);
        Assert.Equal(envelope, restored);
    }

    [Fact]
    public void MaterialsReserved_envelope_round_trips_cleanly()
    {
        var payload = new MaterialsReserved(
            WorkOrderId: Guid.NewGuid(),
            ProductId: "CUSTODIAN-STD",
            Quantity: 2,
            Lines:
            [
                new ReservedComponent("CMP-CHASSIS-STD", 2),
                new ReservedComponent("CMP-BEARING-JEWEL", 12),
            ],
            ReservedUtc: new DateTime(2026, 7, 21, 8, 0, 0, DateTimeKind.Utc));

        var envelope = new EventEnvelope<MaterialsReserved>(
            Guid.NewGuid(), payload.EventType, payload.SchemaVersion,
            Guid.NewGuid(), DateTime.UtcNow, payload);

        var json = JsonSerializer.Serialize(envelope, Options);
        var restored = JsonSerializer.Deserialize<EventEnvelope<MaterialsReserved>>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(payload.WorkOrderId, restored!.Payload.WorkOrderId);
        Assert.Equal(payload.ProductId, restored.Payload.ProductId);
        Assert.Equal(payload.Quantity, restored.Payload.Quantity);
        Assert.Equal(payload.ReservedUtc, restored.Payload.ReservedUtc);
        // The reserved lines are the point of the event — Epic 6 picks up from here.
        Assert.Equal(payload.Lines, restored.Payload.Lines);
    }

    [Fact]
    public void Event_type_and_default_schema_version_are_the_published_contract()
    {
        // Guards against an accidental rename/version bump of the wire contract.
        Assert.Equal("work-order.created", new WorkOrderCreated(Guid.NewGuid(), "p", "P", 1, "r", DateTime.UtcNow).EventType);
        Assert.Equal("work-order.scheduled", new WorkOrderScheduled(Guid.NewGuid(), "p", "P", 1, DateTime.UtcNow).EventType);
        Assert.Equal("work-order.materials-reserved", new MaterialsReserved(Guid.NewGuid(), "p", 1, [], DateTime.UtcNow).EventType);
        Assert.Equal(1, new WorkOrderScheduled(Guid.NewGuid(), "p", "P", 1, DateTime.UtcNow).SchemaVersion);
    }

    [Fact]
    public void Envelope_metadata_is_self_describing_in_the_json()
    {
        var payload = new WorkOrderScheduled(Guid.NewGuid(), "p", "P", 1, DateTime.UtcNow);
        var envelope = new EventEnvelope<WorkOrderScheduled>(
            Guid.NewGuid(), payload.EventType, payload.SchemaVersion, Guid.NewGuid(), DateTime.UtcNow, payload);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(envelope, Options));
        var root = doc.RootElement;

        // A reader can route on type/version/correlation without deserializing the payload.
        Assert.Equal("work-order.scheduled", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("correlationId", out _));
        Assert.True(root.TryGetProperty("payload", out _));
    }
}

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
    public void ProductionCompleted_envelope_round_trips_cleanly()
    {
        var payload = new ProductionCompleted(
            WorkOrderId: Guid.NewGuid(),
            ProductId: "CUSTODIAN-STD",
            SerialNumbers: [Guid.NewGuid(), Guid.NewGuid()],
            AttemptNumber: 2,
            CompletedUtc: new DateTime(2026, 7, 21, 10, 0, 0, DateTimeKind.Utc));

        var restored = RoundTrip(payload);

        Assert.Equal(payload.WorkOrderId, restored.WorkOrderId);
        // The attempt number is the inspection stage's dedupe key — losing it on the wire
        // would silently break idempotency rather than fail loudly.
        Assert.Equal(2, restored.AttemptNumber);
        Assert.Equal(payload.SerialNumbers, restored.SerialNumbers);
    }

    [Fact]
    public void ReworkRequired_envelope_round_trips_cleanly()
    {
        var scrapped = Guid.NewGuid();
        var payload = new ReworkRequired(
            WorkOrderId: Guid.NewGuid(),
            ProductId: "DELVER-MINE",
            Scrapped: [new ScrappedUnit(scrapped, "cracked mainspring")],
            OutstandingQty: 1,
            AttemptNumber: 1,
            RequiredUtc: new DateTime(2026, 7, 21, 11, 0, 0, DateTimeKind.Utc));

        var restored = RoundTrip(payload);

        Assert.Equal(1u, restored.OutstandingQty);
        Assert.Equal(1, restored.AttemptNumber);
        // Scrap reasons are the audit trail of the rework cycle; they have to survive the wire.
        Assert.Equal(scrapped, restored.Scrapped.Single().SerialNumber);
        Assert.Equal("cracked mainspring", restored.Scrapped.Single().Reason);
    }

    [Fact]
    public void InspectionPassed_and_WorkOrderFaulted_envelopes_round_trip_cleanly()
    {
        var passed = new InspectionPassed(Guid.NewGuid(), "SCRIVENER-PRO", [Guid.NewGuid()], DateTime.UtcNow);
        Assert.Equal(passed.SerialNumbers, RoundTrip(passed).SerialNumbers);

        var faulted = new WorkOrderFaulted(Guid.NewGuid(), "SCRIVENER-PRO", "Rebuild cap of 3 exceeded.", 4, DateTime.UtcNow);
        var restoredFault = RoundTrip(faulted);
        Assert.Equal("Rebuild cap of 3 exceeded.", restoredFault.Reason);
        Assert.Equal(4, restoredFault.AttemptNumber);
    }

    [Fact]
    public void ShipmentScheduled_and_WorkOrderCompleted_envelopes_round_trip_cleanly()
    {
        var serials = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var scheduled = new ShipmentScheduled(
            WorkOrderId: Guid.NewGuid(),
            ProductId: "CUSTODIAN-STD",
            Carrier: "Ravenscroft Haulage",
            TrackingNumber: "RH-A1B2C3D4E5",
            SerialNumbers: serials,
            EstimatedArrivalUtc: new DateTime(2026, 7, 25, 9, 0, 0, DateTimeKind.Utc),
            BookedUtc: new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc));

        var restoredSchedule = RoundTrip(scheduled);
        Assert.Equal("Ravenscroft Haulage", restoredSchedule.Carrier);
        Assert.Equal("RH-A1B2C3D4E5", restoredSchedule.TrackingNumber);
        // The parcel's contents are the point of the event; the dashboard renders them.
        Assert.Equal(serials, restoredSchedule.SerialNumbers);
        Assert.Equal(scheduled.EstimatedArrivalUtc, restoredSchedule.EstimatedArrivalUtc);

        var completed = new WorkOrderCompleted(
            Guid.NewGuid(), "CUSTODIAN-STD", "Meridian Aether Post", "MAP-99887766",
            serials, new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc));

        var restoredCompletion = RoundTrip(completed);
        Assert.Equal("MAP-99887766", restoredCompletion.TrackingNumber);
        Assert.Equal(serials, restoredCompletion.SerialNumbers);
    }

    [Fact]
    public void Event_type_and_default_schema_version_are_the_published_contract()
    {
        // Guards against an accidental rename/version bump of the wire contract.
        Assert.Equal("work-order.created", new WorkOrderCreated(Guid.NewGuid(), "p", "P", 1, "r", DateTime.UtcNow).EventType);
        Assert.Equal("work-order.scheduled", new WorkOrderScheduled(Guid.NewGuid(), "p", "P", 1, DateTime.UtcNow).EventType);
        Assert.Equal("work-order.materials-reserved", new MaterialsReserved(Guid.NewGuid(), "p", 1, [], DateTime.UtcNow).EventType);
        Assert.Equal("work-order.production-completed", new ProductionCompleted(Guid.NewGuid(), "p", [], 1, DateTime.UtcNow).EventType);
        Assert.Equal("work-order.rework-required", new ReworkRequired(Guid.NewGuid(), "p", [], 1, 1, DateTime.UtcNow).EventType);
        Assert.Equal("work-order.inspection-passed", new InspectionPassed(Guid.NewGuid(), "p", [], DateTime.UtcNow).EventType);
        Assert.Equal("work-order.faulted", new WorkOrderFaulted(Guid.NewGuid(), "p", "why", 1, DateTime.UtcNow).EventType);
        Assert.Equal("work-order.shipment-scheduled",
            new ShipmentScheduled(Guid.NewGuid(), "p", "c", "t", [], DateTime.UtcNow, DateTime.UtcNow).EventType);
        Assert.Equal("work-order.completed",
            new WorkOrderCompleted(Guid.NewGuid(), "p", "c", "t", [], DateTime.UtcNow).EventType);
        Assert.Equal(1, new WorkOrderScheduled(Guid.NewGuid(), "p", "P", 1, DateTime.UtcNow).SchemaVersion);
    }

    /// <summary>Serializes an event in its envelope exactly as the publisher does, and back.</summary>
    private static T RoundTrip<T>(T payload) where T : IntegrationEvent
    {
        var envelope = new EventEnvelope<T>(
            Guid.NewGuid(), payload.EventType, payload.SchemaVersion,
            Guid.NewGuid(), DateTime.UtcNow, payload);

        var restored = JsonSerializer.Deserialize<EventEnvelope<T>>(
            JsonSerializer.Serialize(envelope, Options), Options);

        Assert.NotNull(restored);
        return restored!.Payload;
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

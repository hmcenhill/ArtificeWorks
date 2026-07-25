using System.Text.Json.Serialization;

namespace ArtificeWorks.Application.Chaos;

/// <summary>
/// The kind of failure a visitor armed against one order (Epic 12).
/// <para>
/// Serialized by <em>name</em> so <c>POST /system/chaos</c> reads and writes
/// <c>"FailInspection"</c> rather than an opaque ordinal. The attribute is scoped to this enum,
/// so it does not widen the API's global numeric-enum convention the way a blanket converter would.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InjectedFaultKind>))]
public enum InjectedFaultKind
{
    /// <summary>Force this order's next inspection to fail, routing it through Epic 6's rework/Fault loop (12.1).</summary>
    FailInspection,

    /// <summary>Reserved for 12.2: a handler throws once, rides 8.2's retry ladder, and recovers on redelivery.</summary>
    TransientOnce,

    /// <summary>Reserved for 12.2: a handler throws a poison exception, parking the message straight into <c>dead_letters</c>.</summary>
    Poison,
}

/// <summary>
/// A fault a visitor has armed against exactly one order — the only new state Epic 12 adds. The API
/// arms it; a different process (the worker) fires it, which is why it is durable shared state and
/// not an in-memory flag. Soft-consumed on fire (<see cref="FiredUtc"/>) rather than deleted, so the
/// dashboard can later show "this order was broken on purpose".
/// </summary>
public sealed record InjectedFault(
    Guid Id,
    Guid WorkOrderId,
    InjectedFaultKind Kind,
    DateTime ArmedUtc,
    string ArmedBy,
    DateTime? FiredUtc);

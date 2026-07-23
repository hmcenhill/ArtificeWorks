using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Api.Errors;

/// <summary>
/// The stable, machine-readable reason codes carried in the <c>code</c> extension
/// of every non-2xx ProblemDetails response. These are part of the public API
/// contract: consumers branch on them, so the string values must not change.
/// </summary>
public static class ProblemCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string WorkOrderNotFound = "work_order_not_found";
    public const string ProductNotFound = "product_not_found";
    public const string ProductAlreadyExists = "product_already_exists";
    public const string TerminalState = "terminal_state";
    public const string MustReleaseFirst = "must_release_first";
    public const string AlreadyHeld = "already_held";
    public const string NotHeld = "not_held";
    public const string InvalidTransition = "invalid_transition";
    public const string AttemptOutOfSequence = "attempt_out_of_sequence";
    public const string InternalError = "internal_error";

    // Inspection verdicts (6.2).
    public const string UnitNotFound = "unit_not_found";
    public const string OrderNotInInspection = "order_not_in_inspection";
    public const string UnitAlreadyInspected = "unit_already_inspected";
    public const string ScrapReasonRequired = "scrap_reason_required";

    // Shipping (7.2, 7.3).
    public const string OrderNotInDelivery = "order_not_in_delivery";
    public const string ShipmentAlreadyBooked = "shipment_already_booked";

    /// <summary>The named carrier isn't one this factory works with — a malformed request (400).</summary>
    public const string UnknownCarrier = "unknown_carrier";

    /// <summary>
    /// The carrier exists, it just won't take the job (409). Deliberately distinct from
    /// <see cref="UnknownCarrier"/>: one is the caller's mistake, the other is the world's.
    /// </summary>
    public const string CarrierUnavailable = "carrier_unavailable";

    public const string NothingToShip = "nothing_to_ship";

    // Recovery (8.3, 8.4).
    public const string DeadLetterNotFound = "dead_letter_not_found";
    public const string DeadLetterAlreadyReplayed = "dead_letter_already_replayed";

    /// <summary>
    /// The same <c>Idempotency-Key</c> arrived with a different body — a client bug, and
    /// deliberately <em>422</em> rather than the 409 that <see cref="ProductAlreadyExists"/>
    /// already means. Nothing here conflicts with the resource's state; the request contradicts
    /// itself, which is what 422 is for.
    /// </summary>
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    /// <summary>
    /// A request with this key is still in flight (or died mid-flight). There is no stored
    /// response to replay yet, and inventing one would be a lie.
    /// </summary>
    public const string IdempotencyKeyInFlight = "idempotency_key_in_flight";

    /// <summary>
    /// A simulation dial was set outside its band (10.2) — a rate above 1.0, a pacing duration
    /// measured in hours, a negative interval. <em>422</em> rather than 400 for the same reason
    /// <see cref="IdempotencyKeyReused"/> is: the request is well-formed and understood, it just
    /// asks for something the factory will not do.
    /// </summary>
    public const string SimulationSettingOutOfRange = "simulation_setting_out_of_range";

    /// <summary>Maps a domain transition-rejection code onto its wire reason code.</summary>
    public static string ForTransition(TransitionErrorCode code) => code switch
    {
        TransitionErrorCode.TerminalState => TerminalState,
        TransitionErrorCode.MustReleaseFirst => MustReleaseFirst,
        TransitionErrorCode.AlreadyHeld => AlreadyHeld,
        TransitionErrorCode.NotHeld => NotHeld,
        TransitionErrorCode.InvalidTransition => InvalidTransition,
        TransitionErrorCode.AttemptOutOfSequence => AttemptOutOfSequence,
        TransitionErrorCode.AlreadyInspected => UnitAlreadyInspected,
        _ => InternalError
    };
}

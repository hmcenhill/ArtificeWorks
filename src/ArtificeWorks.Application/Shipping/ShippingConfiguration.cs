namespace ArtificeWorks.Application.Shipping;

/// <summary>Shipping knobs, bound from the <c>Shipping</c> configuration section.</summary>
public sealed class ShippingConfiguration
{
    public const string SectionName = "Shipping";

    /// <summary>
    /// The virtual carriers this factory works with; a visitor picks one of these by name at
    /// <c>POST /work-orders/{id}/shipments</c>. Left <strong>empty</strong> here on purpose —
    /// <see cref="ConfiguredCarrierBooking"/> falls back to <see cref="DefaultCarriers"/> when
    /// nothing is configured. Seeding the default into this property instead would be a trap:
    /// the configuration binder <em>appends</em> to a pre-populated list, so a factory that
    /// named its own carriers would get them plus ours.
    /// </summary>
    public List<string> Carriers { get; set; } = [];

    /// <summary>
    /// The carriers a factory gets if it names none. In-world names, because the demo is a story
    /// as much as a system.
    /// </summary>
    public static IReadOnlyList<string> DefaultCarriers { get; } =
    [
        "Ravenscroft Haulage",
        "Meridian Aether Post",
        "Kettleby & Sons Carriage"
    ];

    /// <summary>Days from booking to the (entirely virtual) estimated arrival.</summary>
    public int TransitDays { get; set; } = 3;

    /// <summary>
    /// Probability that a carrier refuses the job, 0.0–1.0. Defaults to <c>0.0</c> so the
    /// unattended pipeline runs from creation to Completed with no human action. Raise it to
    /// watch an order stall at Delivery and be rescued by a release (7.3).
    /// </summary>
    public double RefusalRate { get; set; }

    /// <summary>The reason recorded against an order a carrier turns away.</summary>
    public string RefusalReason { get; set; } = "No carrier capacity available.";

    /// <summary>
    /// Optional seed for the booking source's random number generator — the only way to assert
    /// on a coin flip, and the same trick <c>Inspection:Seed</c> plays.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>
    /// Whether the shipping consumer books a carrier automatically. Turn it off and a passed
    /// order rests in Delivery waiting for a visitor to choose — the stage's decision moment,
    /// mirroring <c>Inspection:AutoInspect</c>. Both paths book through the same code, so the
    /// end state is indistinguishable.
    /// </summary>
    public bool AutoBook { get; set; } = true;
}

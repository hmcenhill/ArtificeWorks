using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Shipping;

namespace ArtificeWorks.Application.Data;

/// <summary>
/// A work order's whole story in one chronological list — states, the pick, each build attempt,
/// each inspection and its per-unit verdicts, the booking and the dispatch.
/// <para>
/// <strong>What this is not:</strong> it is <em>what happened</em>, derived from the records the
/// system already keeps, not the literal message log. Nothing here proves a message flowed; a
/// dropped event that stalled the pipeline shows up as an absence, not as an error. Correlation
/// ids would let it link to the log, but state history does not store one — that is a schema
/// change in service of Epic 9's observability, flagged there rather than smuggled in here.
/// </para>
/// <para>
/// Read-only and unpaginated. A work order's timeline is bounded by its rebuild cap; if it ever
/// isn't, that is a symptom worth seeing rather than paging over.
/// </para>
/// </summary>
public class WorkOrderTimelineDto
{
    public Guid WorkOrderId { get; set; }
    public List<TimelineEntryDto> Entries { get; set; } = [];

    public WorkOrderTimelineDto() { }

    public WorkOrderTimelineDto(TimelineData data)
    {
        var workOrder = data.WorkOrder;
        WorkOrderId = workOrder.Id;

        var entries = new List<TimelineEntryDto>();

        foreach (var change in workOrder.StateHistory)
        {
            entries.Add(new TimelineEntryDto(
                change.ChangedUtc, TimelineKind.State, change.CompletedBy,
                change.Notes is { Length: > 0 } notes ? $"{change.Status}: {notes}" : $"{change.Status}",
                new { status = change.Status.ToString(), notes = change.Notes }));
        }

        if (data.Reservation is { } reservation)
        {
            entries.Add(new TimelineEntryDto(
                reservation.ReservedUtc, TimelineKind.Pick, by: null,
                $"Materials picked: {reservation.Describe()}.",
                new
                {
                    lines = reservation.Lines
                        .Select(line => new { componentId = line.ComponentId, quantity = line.Quantity })
                        .ToList()
                }));
        }

        foreach (var run in data.ProductionRuns)
        {
            entries.Add(new TimelineEntryDto(
                run.BuiltUtc, TimelineKind.Build, by: null,
                $"Attempt {run.AttemptNumber} built {run.UnitsBuilt} unit(s).",
                new
                {
                    attemptNumber = run.AttemptNumber,
                    unitsBuilt = run.UnitsBuilt,
                    serialNumbers = workOrder.AssignedStock
                        .Where(unit => unit.BuildAttempt == run.AttemptNumber)
                        .Select(unit => unit.SerialNumber)
                        .ToList()
                }));
        }

        foreach (var run in data.InspectionRuns)
        {
            entries.Add(new TimelineEntryDto(
                run.InspectedUtc, TimelineKind.Inspection, by: null,
                $"Attempt {run.AttemptNumber} inspected: {run.UnitsPassed} passed, {run.UnitsScrapped} scrapped.",
                new
                {
                    attemptNumber = run.AttemptNumber,
                    unitsPassed = run.UnitsPassed,
                    unitsScrapped = run.UnitsScrapped
                }));
        }

        // Per-unit verdicts get their own entries because "which unit failed, and why" is the
        // most interesting thing the timeline knows — and the thing four separate endpoints
        // currently make a caller stitch together.
        foreach (var unit in workOrder.AssignedStock.Where(unit => unit.InspectedUtc is not null))
        {
            entries.Add(new TimelineEntryDto(
                unit.InspectedUtc!.Value, TimelineKind.Verdict, by: null,
                unit.Status == UnitStatus.Passed
                    ? $"Unit {unit.SerialNumber} passed inspection."
                    : $"Unit {unit.SerialNumber} scrapped: {unit.ScrapReason}",
                new
                {
                    serialNumber = unit.SerialNumber,
                    status = unit.Status.ToString(),
                    buildAttempt = unit.BuildAttempt,
                    scrapReason = unit.ScrapReason
                }));
        }

        if (data.Shipment is { } shipment)
        {
            entries.Add(new TimelineEntryDto(
                shipment.BookedUtc, TimelineKind.Shipment, by: null,
                $"Shipment booked: {shipment.Describe()}.",
                ShipmentDetail(shipment, "booked")));

            if (shipment.DispatchedUtc is { } dispatchedUtc)
            {
                entries.Add(new TimelineEntryDto(
                    dispatchedUtc, TimelineKind.Shipment, by: null,
                    $"Shipment dispatched with {shipment.Carrier}, tracking {shipment.TrackingNumber}.",
                    ShipmentDetail(shipment, "dispatched")));
            }
        }

        // Strictly chronological, with a stable kind tiebreak so entries written in the same
        // SaveChanges (a build and its transition, say) don't reorder between requests.
        Entries = entries
            .OrderBy(entry => entry.At)
            .ThenBy(entry => Rank(entry.Kind))
            .ToList();
    }

    private static object ShipmentDetail(Shipment shipment, string stage) => new
    {
        stage,
        carrier = shipment.Carrier,
        trackingNumber = shipment.TrackingNumber,
        status = shipment.Status.ToString(),
        estimatedArrivalUtc = shipment.EstimatedArrivalUtc,
        serialNumbers = shipment.Lines.Select(line => line.SerialNumber).ToList()
    };

    private static int Rank(string kind) => kind switch
    {
        TimelineKind.State => 0,
        TimelineKind.Pick => 1,
        TimelineKind.Build => 2,
        TimelineKind.Inspection => 3,
        TimelineKind.Verdict => 4,
        _ => 5
    };
}

/// <summary>
/// The stable entry kinds a dashboard switches on. Strings rather than an enum because they are
/// a wire contract for a TypeScript client, and because Epic 8's outbox is expected to add an
/// <c>event</c> kind to this same list without a breaking renumber.
/// </summary>
public static class TimelineKind
{
    public const string State = "state";
    public const string Pick = "pick";
    public const string Build = "build";
    public const string Inspection = "inspection";
    public const string Verdict = "verdict";
    public const string Shipment = "shipment";
}

/// <summary>
/// One thing that happened. A discriminated list renders as a single column with typed icons;
/// parallel arrays per kind would not.
/// </summary>
public class TimelineEntryDto
{
    public DateTime At { get; set; }

    /// <summary>One of <see cref="TimelineKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Who did it, where the record names someone. Only state changes do.</summary>
    public string? By { get; set; }

    public string Summary { get; set; } = string.Empty;

    /// <summary>The per-kind payload. Typed by <see cref="Kind"/>, not by the schema.</summary>
    public object? Detail { get; set; }

    public TimelineEntryDto() { }

    public TimelineEntryDto(DateTime at, string kind, string? by, string summary, object? detail)
    {
        At = at;
        Kind = kind;
        By = by;
        Summary = summary;
        Detail = detail;
    }
}

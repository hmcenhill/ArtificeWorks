using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.Materials;

/// <summary>
/// The material-picking workflow: given a scheduled work order, expand its product's BOM into
/// component demand, reserve that demand all-or-nothing, record the pick, and hand the
/// pipeline on to production.
/// <para>
/// It lives in the Application layer rather than in the worker's event handler so the workflow
/// is testable without a broker, and so a future API/manual "pick now" path can reuse it. The
/// worker handler is a thin adapter: envelope in, this service out.
/// </para>
/// <para><strong>Outcomes and their message semantics.</strong> Every outcome here is a
/// <em>handled</em> one — the caller acks. Insufficient stock is a business result (the order
/// goes OnHold with a reason), not a transient fault, and a duplicate delivery is by definition
/// already handled. Nacks stay reserved for genuine faults (a dropped connection, a bug), which
/// since 8.2 means the retry ladder rather than a silent drop.
/// </para>
/// </summary>
public sealed class MaterialPickingService
{
    /// <summary>Author recorded against state-history entries this workflow writes.</summary>
    public const string Author = "picking-worker";

    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IProductRepository _productRepository;
    private readonly IMaterialReservationRepository _reservationRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<MaterialPickingService> _logger;

    public MaterialPickingService(
        IWorkOrderRepository workOrderRepository,
        IProductRepository productRepository,
        IMaterialReservationRepository reservationRepository,
        IEventPublisher eventPublisher,
        ILogger<MaterialPickingService> logger)
    {
        _workOrderRepository = workOrderRepository;
        _productRepository = productRepository;
        _reservationRepository = reservationRepository;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<PickResult> PickMaterials(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            _logger.LogWarning("Picking requested for unknown work order {WorkOrderId}; nothing to pick.", workOrderId);
            return new PickResult(PickOutcome.WorkOrderNotFound, $"No work order found with id {workOrderId}.");
        }

        // Cheap pre-check for the common duplicate case. It is NOT the guarantee — two
        // deliveries can both pass it concurrently — the unique index on the reservation's
        // work order id is what actually enforces once-per-order. See TryReserve.
        var existing = await _reservationRepository.GetForWorkOrder(workOrderId, cancellationToken);
        if (existing is not null)
        {
            return AlreadyPicked(workOrderId, existing.ReservedUtc);
        }

        var product = await _productRepository.GetWithBom(workOrder.OrderedItem.ItemId);
        var demand = product?.ComputeDemand(workOrder.OrderItemQty) ?? [];
        if (demand.Count == 0)
        {
            // A product with no BOM isn't an error — nothing is consumed to build it — but it
            // is worth surfacing, because in a seeded factory it almost certainly means the
            // catalog is incomplete.
            _logger.LogWarning(
                "Product {ProductId} has no bill of materials; work order {WorkOrderId} reserves nothing.",
                workOrder.OrderedItem.ItemId, workOrderId);
            return new PickResult(PickOutcome.NoBillOfMaterials,
                $"Product {workOrder.OrderedItem.ItemId} has no bill of materials; nothing to reserve.");
        }

        // The note and the MaterialsReserved event are staged *inside* the reservation
        // transaction (8.1): the pick, the audit line describing it and the announcement of it
        // now commit as one. Before this, the note was a second save and the publish was a
        // best-effort call after the commit — so a crash in between could leave inventory drawn
        // with nothing downstream ever hearing about it, and the order stalled at Scheduled with
        // no retry. That was the demo's worst failure mode: silence.
        var commit = await _reservationRepository.TryReserve(
            workOrderId,
            demand,
            stageWithReservation: reservation => StagePickAnnouncement(workOrder, demand, reservation, cancellationToken),
            cancellationToken);

        return commit.Outcome switch
        {
            ReservationOutcome.Reserved => OnReserved(workOrder, demand, commit.Reservation!),
            ReservationOutcome.InsufficientStock => await OnShort(workOrder, commit.ShortComponentIds ?? []),
            ReservationOutcome.AlreadyReserved => AlreadyPicked(workOrderId, reservedUtc: null),
            _ => throw new InvalidOperationException($"Unhandled reservation outcome {commit.Outcome}.")
        };
    }

    /// <summary>
    /// Everything that describes a successful pick but isn't the draw itself: the state-history
    /// note and the hand-off to production. Called by the repository from inside the reservation
    /// transaction, so all of it is flushed by the same <c>SaveChanges</c> that inserts the
    /// reservation row — and rolled back with it if a concurrent delivery wins the unique index.
    /// </summary>
    private async Task StagePickAnnouncement(
        Domain.Models.WorkOrder workOrder,
        IReadOnlyList<ComponentDemand> demand,
        MaterialReservation reservation,
        CancellationToken cancellationToken)
    {
        workOrder.AppendNote(Author, Truncate($"Materials picked: {reservation.Describe()}."));

        await _eventPublisher.PublishAsync(new MaterialsReserved(
            workOrder.Id,
            workOrder.OrderedItem.ItemId,
            workOrder.OrderItemQty,
            demand.Select(d => new ReservedComponent(d.ComponentId, d.Quantity)).ToList(),
            reservation.ReservedUtc), cancellationToken);
    }

    private PickResult OnReserved(
        Domain.Models.WorkOrder workOrder,
        IReadOnlyList<ComponentDemand> demand,
        MaterialReservation reservation)
    {
        var summary = $"Materials picked: {reservation.Describe()}.";

        _logger.LogInformation(
            "Reserved {LineCount} component line(s) for work order {WorkOrderId}: {Reserved}",
            demand.Count, workOrder.Id, reservation.Describe());

        return new PickResult(PickOutcome.Picked, summary, demand);
    }

    private async Task<PickResult> OnShort(Domain.Models.WorkOrder workOrder, IReadOnlyList<string> shortComponentIds)
    {
        var summary = shortComponentIds.Count == 0
            ? "Insufficient stock; no materials reserved."
            : $"Insufficient stock for {string.Join(", ", shortComponentIds)}; no materials reserved.";

        // Nothing was drawn — the reservation transaction rolled back — so the order simply
        // waits. Releasing the hold (once stock arrives) re-runs picking in a later epic;
        // for now a human releases it via the existing endpoint.
        var hold = workOrder.SetHold(Author, Truncate(summary));
        if (!hold.Success)
        {
            // e.g. the order was already held or cancelled between scheduling and picking.
            _logger.LogWarning(
                "Work order {WorkOrderId} was short of stock but could not be held ({Code}): {Error}",
                workOrder.Id, hold.Code, hold.Error);
        }
        else
        {
            _logger.LogInformation("Work order {WorkOrderId} placed OnHold: {Reason}", workOrder.Id, summary);
        }

        await _workOrderRepository.Update(workOrder);
        return new PickResult(PickOutcome.InsufficientStock, summary);
    }

    /// <summary>
    /// A redelivery. Nothing is written — deliberately not even a state-history note, since a
    /// note per redelivery would itself be a non-idempotent side effect — but it IS logged, so
    /// idempotency is observable when Epic 12 lets a visitor redeliver a message on purpose.
    /// </summary>
    private PickResult AlreadyPicked(Guid workOrderId, DateTime? reservedUtc)
    {
        var summary = reservedUtc is null
            ? $"Work order {workOrderId} was picked concurrently by another delivery; skipping duplicate."
            : $"Work order {workOrderId} was already picked at {reservedUtc:O}; skipping duplicate.";

        _logger.LogInformation("Duplicate pick skipped (idempotent): {Summary}", summary);
        return new PickResult(PickOutcome.AlreadyPicked, summary);
    }

    // State-history notes are capped at 500 chars by the schema; a wide BOM can exceed that.
    private static string Truncate(string note) => note.Length <= 500 ? note : note[..497] + "...";
}

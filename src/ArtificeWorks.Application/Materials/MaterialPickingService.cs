using System.Diagnostics;

using ArtificeWorks.Application.Chaos;
using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Application.SubAssemblies;
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
/// <para>
/// <strong>One entry point, two triggers</strong> (13.1). <c>work-order.scheduled</c> picks for
/// attempt 1 and the whole ordered quantity; <c>work-order.rework-required</c> picks for attempt
/// N+1 and only the outstanding quantity. Nothing else differs — a rebuild goes through the same
/// draw, the same all-or-nothing rule and the same hand-off to production as the original build,
/// which is what makes the rework loop a real cycle rather than a shortcut past the expensive
/// stage.
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

    /// <summary>The attempt a freshly scheduled order picks for.</summary>
    public const int InitialAttempt = 1;

    private readonly IWorkOrderRepository _workOrderRepository;
    private readonly IProductRepository _productRepository;
    private readonly IMaterialReservationRepository _reservationRepository;
    private readonly IEventPublisher _eventPublisher;
    private readonly ArtificeWorksMetrics _metrics;
    private readonly IInjectedFaultRepository? _injectedFaults;
    private readonly SubAssemblyService? _subAssemblies;
    private readonly ILogger<MaterialPickingService> _logger;

    /// <param name="injectedFaults">
    /// Epic 12's fault registry. Picking is where the two <em>broker-facing</em> faults fire (12.2):
    /// a visitor can arm this order to throw once mid-pick (transient → retry ladder → recover) or to
    /// be declared unprocessable (poison → parked → dead letter). Null (a unit test, or any host
    /// without chaos wired) means no order is ever broken here.
    /// </param>
    /// <param name="subAssemblies">
    /// 13.3's spawn. Null (a unit test of the pick in isolation) means a short made component is
    /// treated exactly like a short bought one — the order simply holds, which is the behaviour
    /// every epic before this one had.
    /// </param>
    public MaterialPickingService(
        IWorkOrderRepository workOrderRepository,
        IProductRepository productRepository,
        IMaterialReservationRepository reservationRepository,
        IEventPublisher eventPublisher,
        ArtificeWorksMetrics metrics,
        ILogger<MaterialPickingService> logger,
        IInjectedFaultRepository? injectedFaults = null,
        SubAssemblyService? subAssemblies = null)
    {
        _workOrderRepository = workOrderRepository;
        _productRepository = productRepository;
        _reservationRepository = reservationRepository;
        _eventPublisher = eventPublisher;
        _metrics = metrics;
        _injectedFaults = injectedFaults;
        _subAssemblies = subAssemblies;
        _logger = logger;
    }

    /// <summary>
    /// The initial pick: attempt 1, for the whole ordered quantity. What
    /// <see cref="Messaging.Events.WorkOrderScheduled"/> means.
    /// </summary>
    public Task<PickResult> PickMaterials(Guid workOrderId, CancellationToken cancellationToken = default)
        => PickMaterials(workOrderId, InitialAttempt, demandQty: null, cancellationToken);

    /// <param name="attemptNumber">
    /// Which build attempt this pick supplies. <strong>Derived by the caller from the event</strong>
    /// (1 for scheduled, N+1 for rework of attempt N) and never read from the order's current
    /// state — for the same reason 6.4 derives production's attempt: a redelivery must compute the
    /// same number, or the unique index would be guarding two different things.
    /// </param>
    /// <param name="demandQty">
    /// How many finished units this pick buys parts for. <c>null</c> means the whole ordered
    /// quantity, which is only ever right for the initial pick. A rebuild passes
    /// <see cref="Messaging.Events.ReworkRequired.OutstandingQty"/> — taken from the event rather
    /// than re-read off the order for exactly the reason above: two deliveries of one rework event
    /// must request an identical draw.
    /// </param>
    public async Task<PickResult> PickMaterials(
        Guid workOrderId,
        int attemptNumber,
        uint? demandQty,
        CancellationToken cancellationToken = default)
    {
        // Makes the consumer span findable by the one identifier a visitor has (9.1).
        ArtificeWorksTelemetry.StampWorkOrder(workOrderId);
        Activity.Current?.SetTag(ArtificeWorksTelemetry.AttemptAttribute, attemptNumber);

        // 12.2: the single choke point for the stage. Fire any armed broker fault here — before the
        // pick's work opens a transaction — and throw. Because the consume is its own committed write
        // (TryConsume runs outside any stage transaction), the throw that follows rolls back nothing
        // and the redelivery finds the fault disarmed, which is what makes a transient recover instead
        // of re-firing forever. The throw lands in the consumer's existing failure taxonomy; there is
        // no chaos-mode code path.
        await FireBrokerFaultIfArmed(workOrderId, cancellationToken);

        var workOrder = await _workOrderRepository.GetWithHistory(workOrderId);
        if (workOrder is null)
        {
            _logger.LogWarning("Picking requested for unknown work order {WorkOrderId}; nothing to pick.", workOrderId);
            return new PickResult(PickOutcome.WorkOrderNotFound, $"No work order found with id {workOrderId}.");
        }

        // Cheap pre-check for the common duplicate case. It is NOT the guarantee — two
        // deliveries can both pass it concurrently — the unique index on the reservation's
        // (work order id, attempt) is what actually enforces once-per-attempt. See TryReserve.
        var existing = await _reservationRepository.GetForAttempt(workOrderId, attemptNumber, cancellationToken);
        if (existing is not null)
        {
            return AlreadyPicked(workOrderId, attemptNumber, existing.ReservedUtc);
        }

        // Null means "the initial pick, for everything ordered". A rebuild was handed its number by
        // the event that asked for it, so nothing here re-derives what the order currently needs.
        var quantity = demandQty ?? workOrder.OrderItemQty;
        if (quantity == 0)
        {
            // Only reachable from a rework event that asked for nothing, which inspection does not
            // publish — it goes to rework precisely because units are outstanding. Treated as a
            // handled no-op rather than an exception: nacking would put a message that can never
            // succeed onto the retry ladder.
            _logger.LogWarning(
                "Pick requested for work order {WorkOrderId} attempt {Attempt} with a demand of zero units; "
                + "nothing to reserve.", workOrderId, attemptNumber);
            return new PickResult(PickOutcome.NothingToPick,
                $"Work order {workOrderId} needs no units on attempt {attemptNumber}; nothing to reserve.");
        }

        var product = await _productRepository.GetWithBom(workOrder.OrderedItem.ItemId);
        var demand = product?.ComputeDemand(quantity) ?? [];
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
            attemptNumber,
            demand,
            stageWithReservation: reservation =>
                StagePickAnnouncement(workOrder, attemptNumber, quantity, demand, reservation, cancellationToken),
            cancellationToken);

        return commit.Outcome switch
        {
            ReservationOutcome.Reserved => OnReserved(workOrder, demand, commit.Reservation!),
            ReservationOutcome.InsufficientStock =>
                await OnShort(workOrder, attemptNumber, commit.ShortComponents ?? [], product!, cancellationToken),
            ReservationOutcome.AlreadyReserved => AlreadyPicked(workOrderId, attemptNumber, reservedUtc: null),
            _ => throw new InvalidOperationException($"Unhandled reservation outcome {commit.Outcome}.")
        };
    }

    /// <summary>
    /// Fires an armed broker fault against this order, exactly once (12.2). A no-op — and a single
    /// cheap indexed lookup — when nothing is armed, which is almost always. When one is armed, this
    /// throws, and the kind decides where the message goes:
    /// <list type="bullet">
    ///   <item><description><see cref="InjectedFaultKind.TransientOnce"/>: an ordinary throw the
    ///     consumer classifies as transient. The order stays Scheduled, the message climbs a rung of
    ///     8.2's ladder, and the redelivery re-runs this method with the fault now disarmed —
    ///     completing the pick instead of re-firing.</description></item>
    ///   <item><description><see cref="InjectedFaultKind.Poison"/>: the consumer parks it straight
    ///     into <c>dead_letters</c>, no retries, where a human replays it (8.3). The replayed message
    ///     also finds the fault disarmed.</description></item>
    /// </list>
    /// The consume (<see cref="IInjectedFaultRepository.TryConsume"/>) is a standalone committed
    /// write, run here before any of the pick's work opens a transaction, so a rolled-back stage
    /// cannot un-fire it — the one subtle correctness point of the epic.
    /// <para>
    /// 12.2 was designed when a pick happened once per order; since 13.1 it happens once per
    /// attempt, so this runs several times for a rebuilding order. Nothing changes: <c>TryConsume</c>
    /// is a one-shot conditional update, so an armed fault still fires on exactly one of those
    /// picks. That was an unstated premise rather than a claim, which is why there is now a test
    /// for it rather than a paragraph.
    /// </para>
    /// </summary>
    private async Task FireBrokerFaultIfArmed(Guid workOrderId, CancellationToken cancellationToken)
    {
        if (_injectedFaults is null)
        {
            return;
        }

        if (await _injectedFaults.TryConsume(workOrderId, InjectedFaultKind.TransientOnce, cancellationToken))
        {
            // Warning, not Error: a fired transient is the pipeline about to visibly recover, which is
            // exactly the line 12.2's audience is watching for.
            _logger.LogWarning(
                "Work order {WorkOrderId} is failing its pick once by injected fault (transient); "
                + "the message will climb the retry ladder and recover on redelivery.", workOrderId);

            throw new InjectedFaultException(InjectedFaultKind.TransientOnce,
                $"Injected transient fault: work order {workOrderId} failed its pick on purpose (Epic 12.2). "
                + "It recovers on redelivery.");
        }

        if (await _injectedFaults.TryConsume(workOrderId, InjectedFaultKind.Poison, cancellationToken))
        {
            _logger.LogWarning(
                "Work order {WorkOrderId} is being poisoned by injected fault; the message will park "
                + "straight into dead_letters awaiting a replay.", workOrderId);

            throw new InjectedFaultException(InjectedFaultKind.Poison,
                $"Injected poison fault: work order {workOrderId} was declared unprocessable on purpose (Epic 12.2).");
        }
    }

    /// <summary>
    /// Everything that describes a successful pick but isn't the draw itself: the state-history
    /// note and the hand-off to production. Called by the repository from inside the reservation
    /// transaction, so all of it is flushed by the same <c>SaveChanges</c> that inserts the
    /// reservation row — and rolled back with it if a concurrent delivery wins the unique index.
    /// </summary>
    private async Task StagePickAnnouncement(
        Domain.Models.WorkOrder workOrder,
        int attemptNumber,
        uint quantity,
        IReadOnlyList<ComponentDemand> demand,
        MaterialReservation reservation,
        CancellationToken cancellationToken)
    {
        workOrder.AppendNote(Author, Truncate($"Materials picked: {reservation.Describe()}."));

        await _eventPublisher.PublishAsync(new MaterialsReserved(
            workOrder.Id,
            workOrder.OrderedItem.ItemId,
            // The quantity this pick bought parts for, not the order's — on a rebuild those differ,
            // and the number that describes the lines beside it is the useful one.
            quantity,
            attemptNumber,
            demand.Select(d => new ReservedComponent(d.ComponentId, d.Quantity)).ToList(),
            reservation.ReservedUtc), cancellationToken);
    }

    private PickResult OnReserved(
        Domain.Models.WorkOrder workOrder,
        IReadOnlyList<ComponentDemand> demand,
        MaterialReservation reservation)
    {
        var summary = $"Materials picked: {reservation.Describe()}.";

        _metrics.Pick("picked");

        _logger.LogInformation(
            "Reserved {LineCount} component line(s) for work order {WorkOrderId}: {Reserved}",
            demand.Count, workOrder.Id, reservation.Describe());

        return new PickResult(PickOutcome.Picked, summary, demand);
    }

    /// <summary>
    /// The pick came up short. Since 13.3 that has two endings rather than one, and which applies is
    /// decided entirely by whether the missing part is something the factory <em>makes</em>:
    /// <list type="bullet">
    ///   <item><description><strong>Bought parts short</strong> — the order goes OnHold with a
    ///     reason, exactly as it has since 5.3. Unchanged, and still the common case.</description></item>
    ///   <item><description><strong>A made component short</strong> — a child work order is
    ///     scheduled for the shortfall, and the parent holds naming what it is waiting for. The
    ///     child's completion is what releases it.</description></item>
    /// </list>
    /// The hold, the children and the events announcing them are one commit: the children were
    /// attached to the parent's tracked graph, so the save below writes all of it or none of it.
    /// </summary>
    private async Task<PickResult> OnShort(
        Domain.Models.WorkOrder workOrder,
        int attemptNumber,
        IReadOnlyList<ShortComponent> shortages,
        Product product,
        CancellationToken cancellationToken)
    {
        // The attempt is named from the rebuild onwards. "Insufficient stock" against an order that
        // is already InProcess with units on the floor reads like a contradiction otherwise — the
        // reader needs to know it is the *rebuild* that cannot get parts, not the original build.
        var scope = attemptNumber == InitialAttempt
            ? "Insufficient stock"
            : $"Insufficient stock for rebuild attempt {attemptNumber}";

        var summary = shortages.Count == 0
            ? $"{scope}; no materials reserved."
            : $"{scope}: {string.Join(", ", shortages.Select(shortage => shortage.ComponentId))}; "
              + "no materials reserved.";

        _metrics.Pick("insufficient_stock");

        // Planned, not committed: the children are built in memory and their announcements staged,
        // so the hold below can name what the order is waiting for *before* anything is written.
        var plan = _subAssemblies is null
            ? SubAssemblyPlan.Nothing("Sub-assembly scheduling is not wired into this host.")
            : await _subAssemblies.PlanForShortages(
                workOrder, attemptNumber, shortages, product.BillOfMaterials, cancellationToken);

        if (plan.Outcome == SubAssemblyRequestOutcome.TooDeep)
        {
            // The chain has run as deep as it is allowed to. Faulting is the honest ending: the
            // catalog is wrong, no amount of waiting fixes it, and an order held forever is a
            // stall that looks like a bug. 13.2 refuses the same shape on the read side.
            return await OnTooDeep(workOrder, plan.Summary);
        }

        // Nothing was drawn — the reservation transaction rolled back — so the order simply waits.
        // What it waits *for* is the only thing 13.3 changed here.
        if (plan.Outcome is SubAssemblyRequestOutcome.Requested or SubAssemblyRequestOutcome.AlreadyRequested)
        {
            summary = $"{summary} Waiting on sub-assembly: {plan.Summary}";
        }

        var from = workOrder.CurrentStatus;
        var hold = workOrder.SetHold(Author, Truncate(summary));
        if (!hold.Success)
        {
            // e.g. the order was already held or cancelled between scheduling and picking. A parent
            // re-picking after a sibling released it and finding a second component still short
            // lands here too.
            _logger.LogWarning(
                "Work order {WorkOrderId} was short of stock but could not be held ({Code}): {Error}",
                workOrder.Id, hold.Code, hold.Error);
        }
        else
        {
            _metrics.Transition(from.ToString(), workOrder.CurrentStatus.ToString(), workOrder.Origin.ToString());

            // Warning, not Information: a hold is the pipeline stopping, and the levelling rule
            // for this epic is that anything a visitor would want to react to is at least Warning.
            _logger.LogWarning("Work order {WorkOrderId} placed OnHold: {Reason}", workOrder.Id, summary);
        }

        // ONE save for the hold, the children and the outbox rows announcing them. The children were
        // attached to the parent's tracked graph by WorkOrder.ForSubAssembly, so they are already in
        // this unit of work — there is no state where a parent is held with nobody building for it,
        // and none where a child exists that nothing announced. With no children this is byte for
        // byte the Update the short path has always done.
        await _workOrderRepository.Update(workOrder);
        _subAssemblies?.RecordPlanOutcome(workOrder, plan);

        return new PickResult(PickOutcome.InsufficientStock, summary);
    }

    /// <summary>
    /// The sub-assembly chain has reached its depth limit (13.3). Combined with 13.2's cycle
    /// refusal, this is the guard that stops a bad catalog from generating work forever — so the
    /// order stops here with a reason a person can read, rather than waiting for a part nothing is
    /// scheduled to build.
    /// </summary>
    private async Task<PickResult> OnTooDeep(Domain.Models.WorkOrder workOrder, string reason)
    {
        var from = workOrder.CurrentStatus;
        var faulted = workOrder.Fault(Author, Truncate(reason));

        if (!faulted.Success)
        {
            _logger.LogWarning(
                "Work order {WorkOrderId} exceeded the sub-assembly depth limit but could not be faulted "
                + "({Code}): {Error}", workOrder.Id, faulted.Code, faulted.Error);
        }
        else
        {
            _metrics.Transition(from.ToString(), workOrder.CurrentStatus.ToString(), workOrder.Origin.ToString());
            _logger.LogError("Work order {WorkOrderId} FAULTED: {Reason}", workOrder.Id, reason);
        }

        await _workOrderRepository.Update(workOrder);
        return new PickResult(PickOutcome.InsufficientStock, reason);
    }

    /// <summary>
    /// A redelivery. Nothing is written — deliberately not even a state-history note, since a
    /// note per redelivery would itself be a non-idempotent side effect — but it IS logged, so
    /// idempotency is observable when Epic 12 lets a visitor redeliver a message on purpose.
    /// </summary>
    private PickResult AlreadyPicked(Guid workOrderId, int attemptNumber, DateTime? reservedUtc)
    {
        var summary = reservedUtc is null
            ? $"Work order {workOrderId} attempt {attemptNumber} was picked concurrently by another delivery; skipping duplicate."
            : $"Work order {workOrderId} attempt {attemptNumber} was already picked at {reservedUtc:O}; skipping duplicate.";

        _metrics.Pick("duplicate");
        _logger.LogInformation("Duplicate pick skipped (idempotent): {Summary}", summary);
        return new PickResult(PickOutcome.AlreadyPicked, summary);
    }

    // State-history notes are capped at 500 chars by the schema; a wide BOM can exceed that.
    private static string Truncate(string note) => note.Length <= 500 ? note : note[..497] + "...";
}

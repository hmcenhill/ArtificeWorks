using ArtificeWorks.Application.Interfaces;
using ArtificeWorks.Application.Materials;
using ArtificeWorks.Application.Messaging;
using ArtificeWorks.Application.Messaging.Events;
using ArtificeWorks.Application.Observability;
using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

using Microsoft.Extensions.Logging;

namespace ArtificeWorks.Application.SubAssemblies;

/// <summary>
/// The factory making what it hasn't got (13.3): a pick short of a <em>manufactured</em> component
/// schedules the sub-assembly instead of simply waiting for one to appear.
/// <para>
/// <strong>Two halves of one loop.</strong> <see cref="RequestForShortages"/> runs inside the short
/// pick and creates the child; <see cref="ReleaseParentOfCompletedChild"/> runs on the child's
/// <c>work-order.completed</c> and puts the parent back on the pipeline. In between, the child is an
/// entirely ordinary work order running the entirely ordinary stages — which is the whole reason
/// this is one small service rather than a second pipeline.
/// </para>
/// <para>
/// <strong>The parent's retry is a re-pick, not a resume.</strong> Releasing the hold and
/// republishing <c>work-order.scheduled</c> reuses the trigger that already exists: there is no
/// "resume" verb and no partial state to reconstruct, because the pick that failed was
/// all-or-nothing and drew nothing. 13.1 made picking attempt-aware, so the retry lands on the same
/// attempt the shortage interrupted, and the reservation index is untroubled.
/// </para>
/// <para>
/// <strong>A re-pick that is still short simply holds again.</strong> When a parent is waiting on
/// two children, the first to finish releases it, the re-pick finds the second component still
/// short, sees a live child for it, and holds without spawning anything. The second child then
/// releases it for real. Two extra picks and two extra history lines buy a loop with no "am I still
/// waiting on anyone?" query in it, and one that self-corrects if the world moves underneath it.
/// </para>
/// </summary>
public sealed class SubAssemblyService
{
    /// <summary>Author recorded against state-history entries this workflow writes.</summary>
    public const string Author = "sub-assembly-worker";

    private readonly IWorkOrderRepository _workOrders;
    private readonly IProductRepository _products;
    private readonly IEventPublisher _eventPublisher;
    private readonly ArtificeWorksMetrics _metrics;
    private readonly ILogger<SubAssemblyService> _logger;

    public SubAssemblyService(
        IWorkOrderRepository workOrders,
        IProductRepository products,
        IEventPublisher eventPublisher,
        ArtificeWorksMetrics metrics,
        ILogger<SubAssemblyService> logger)
    {
        _workOrders = workOrders;
        _products = products;
        _eventPublisher = eventPublisher;
        _metrics = metrics;
        _logger = logger;
    }

    // ------------------------------------------------------------------------ spawn

    /// <summary>
    /// Builds a child work order for every <em>made</em> component this pick came up short of,
    /// attaches them to the parent's tracked graph and stages their announcements — but does not
    /// save. The caller commits, so the parent's hold, the children and the events announcing them
    /// are one unit of work.
    /// <para>
    /// <strong>This is the answer to the question the story left open</strong> (create the child
    /// inside the pick's transaction, or announce it as an event and create it later). The short
    /// path already ends in a save that holds the parent, so joining it costs no second transaction,
    /// no new event type, and no intermediate state where a parent is held with nobody building for
    /// it. A child that exists but was never announced, or was announced but never created, is not
    /// a state this system can reach.
    /// </para>
    /// </summary>
    /// <param name="parent">The order whose pick came up short. Tracked by the caller's context.</param>
    /// <param name="attemptNumber">The pick attempt that came up short.</param>
    /// <param name="shortages">Every component the draw refused, with what was on the shelf.</param>
    /// <param name="bom">
    /// The parent product's BOM lines, whose components carry <see cref="Component.MakeProductId"/>
    /// — already loaded by the pick, so answering "is this one made?" costs no query.
    /// </param>
    public async Task<SubAssemblyPlan> PlanForShortages(
        WorkOrder parent,
        int attemptNumber,
        IReadOnlyList<ShortComponent> shortages,
        IReadOnlyList<BomLine> bom,
        CancellationToken cancellationToken = default)
    {
        var madeById = bom
            .Select(line => line.Component)
            .Where(component => component.MakeProductId is not null)
            .ToDictionary(component => component.ComponentId, StringComparer.Ordinal);

        var made = shortages
            .Where(shortage => madeById.ContainsKey(shortage.ComponentId))
            .OrderBy(shortage => shortage.ComponentId, StringComparer.Ordinal)
            .ToList();

        if (made.Count == 0)
        {
            // Every shortage is a bought part. The order holds exactly as it has since 5.3 —
            // this is the unchanged path, and it is still the common one.
            return SubAssemblyPlan.Nothing("No manufactured components are short; nothing to make.");
        }

        if (!parent.CanSpawnSubAssembly)
        {
            // The runaway guard. A catalog that reaches itself would otherwise generate child orders
            // forever, and this is a shared world with a rate-limited chaos endpoint and no auth.
            var summary =
                $"Sub-assembly chain is already {parent.TreeDepth} level(s) deep, at the "
                + $"{WorkOrder.MaxSubAssemblyDepth}-level limit; refusing to schedule "
                + $"{string.Join(", ", made.Select(shortage => shortage.ComponentId))}.";

            _logger.LogError(
                "Work order {WorkOrderId} is {Depth} sub-assembly level(s) deep and cannot schedule "
                + "another; the bill of materials is too deep or cyclic. {Summary}",
                parent.Id, parent.TreeDepth, summary);

            return SubAssemblyPlan.TooDeep(summary);
        }

        // Cheap pre-check against the same predicate the filtered unique index uses. Not the
        // guarantee — two deliveries can pass it together — but it keeps the common redelivery from
        // building a graph the database is only going to reject.
        var alreadyOpen = (await _workOrders.ListOpenSubAssemblyRequests(parent.Id, attemptNumber, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var children = new List<WorkOrder>();
        var requested = new List<RequestedSubAssembly>();

        foreach (var shortage in made)
        {
            if (alreadyOpen.Contains(shortage.ComponentId))
            {
                continue;
            }

            var makeProductId = madeById[shortage.ComponentId].MakeProductId!;

            // Tracked, not no-tracking: the child holds this product as its OrderedItem, and an
            // untracked instance would be inserted as a *new* catalog row by the caller's save.
            var maker = await _products.Get(makeProductId);
            if (maker is null)
            {
                // 13.2 refuses to explode a BOM whose maker is missing for the same reason: treating
                // it as a bought part would understate demand silently. Here it is worse — the order
                // would wait for stock that nothing is going to produce — so it is logged loudly and
                // the parent falls through to an ordinary hold.
                _logger.LogError(
                    "Component {ComponentId} is made by product {ProductId}, which is not in the catalog; "
                    + "work order {WorkOrderId} cannot schedule it.",
                    shortage.ComponentId, makeProductId, parent.Id);
                continue;
            }

            // The shortfall, not the whole demand: whatever is already on the shelf will be drawn by
            // the re-pick, so building it again would be waste the factory pays for twice.
            var quantity = shortage.Shortfall;
            if (quantity == 0)
            {
                continue;
            }

            var child = WorkOrder.ForSubAssembly(
                parent,
                maker,
                shortage.ComponentId,
                quantity,
                attemptNumber,
                Author,
                $"Making {quantity} × {shortage.ComponentId} for work order {parent.Id} "
                + $"(attempt {attemptNumber}).");

            // Straight past Intake: nothing is waiting on a human to approve a part the factory has
            // already decided it needs. The advance is what publishes `scheduled` below, so the
            // child starts moving as soon as the outbox drains.
            var advance = child.AdvanceToNextStep(Author, "Scheduled by the parent order's short pick.");
            if (!advance.Success)
            {
                // Unreachable — a brand-new order is in Intake — but a silent Intake orphan would be
                // an order nobody ever picks, so it is stated rather than assumed.
                _logger.LogError(
                    "Sub-assembly work order {ChildId} for {ComponentId} could not be scheduled ({Code}): {Error}",
                    child.Id, shortage.ComponentId, advance.Code, advance.Error);
                continue;
            }

            await _eventPublisher.PublishAsync(new WorkOrderCreated(
                child.Id,
                maker.ItemId,
                maker.ItemName,
                quantity,
                Author,
                child.CreatedUtc), cancellationToken);

            await _eventPublisher.PublishAsync(new WorkOrderScheduled(
                child.Id,
                maker.ItemId,
                maker.ItemName,
                quantity,
                MaterialPickingService.InitialAttempt,
                child.UpdatedUtc), cancellationToken);

            children.Add(child);
            requested.Add(new RequestedSubAssembly(child.Id, shortage.ComponentId, maker.ItemId, quantity));
        }

        if (children.Count == 0)
        {
            var waiting = string.Join(", ", made.Select(shortage => shortage.ComponentId));
            return SubAssemblyPlan.AlreadyRequested($"already making {waiting}.");
        }

        var summaryText = string.Join(", ",
            requested.Select(request => $"{request.Quantity} × {request.ComponentId}"));

        return new SubAssemblyPlan(
            SubAssemblyRequestOutcome.Requested, $"making {summaryText}.", children, requested);
    }

    /// <summary>
    /// Records what a committed plan did. Called <em>after</em> the caller's save, because 9.2's
    /// rule is that a counter must not move for work that rolled back — and here the filtered unique
    /// index is what decides whether the work happened at all. A losing duplicate never reaches this
    /// method: its save throws, the message climbs 8.2's ladder, and its redelivery plans nothing.
    /// </summary>
    public void RecordPlanOutcome(WorkOrder parent, SubAssemblyPlan plan)
    {
        if (plan.Requested.Count == 0)
        {
            return;
        }

        foreach (var request in plan.Requested)
        {
            _metrics.WorkOrderCreated(parent.Origin.ToString());

            _logger.LogInformation(
                "Work order {WorkOrderId} is short of made component {ComponentId}; scheduled sub-assembly "
                + "order {ChildId} for {Quantity} × {ProductId} (depth {Depth}).",
                parent.Id, request.ComponentId, request.WorkOrderId, request.Quantity, request.ProductId,
                parent.TreeDepth + 1);
        }
    }

    // ---------------------------------------------------------------------- release

    /// <summary>
    /// A child work order has completed and its output is on the shelf; put the parent back on the
    /// pipeline. The subscriber behind <c>work-order.completed</c> — the first that key has ever had
    /// in the pipeline (since 11.2 it had exactly one reader, the dashboard relay).
    /// </summary>
    public async Task<ParentReleaseResult> ReleaseParentOfCompletedChild(
        Guid completedWorkOrderId,
        CancellationToken cancellationToken = default)
    {
        ArtificeWorksTelemetry.StampWorkOrder(completedWorkOrderId);

        var child = await _workOrders.Get(completedWorkOrderId);
        if (child?.ParentWorkOrderId is not { } parentId)
        {
            // An ordinary customer order finishing. This is the overwhelmingly common case, so it
            // costs one indexed read and returns.
            return new ParentReleaseResult(ParentReleaseOutcome.NotASubAssembly,
                $"Work order {completedWorkOrderId} has no parent; nothing to release.");
        }

        var parent = await _workOrders.GetWithHistory(parentId);
        if (parent is null)
        {
            // 10.4's sweep refuses to retire a parent with a live child, so this means the parent
            // was cancelled and swept, or the database was reset under a message in flight. The
            // units are on the shelf either way — nothing is lost, there is just nobody to tell.
            _logger.LogWarning(
                "Sub-assembly work order {ChildId} completed but its parent {ParentId} is gone; "
                + "the stock stands, but no order is waiting for it.", completedWorkOrderId, parentId);

            return new ParentReleaseResult(ParentReleaseOutcome.ParentGone,
                $"Parent work order {parentId} no longer exists.");
        }

        if (parent.CurrentStatus != WorkOrderStatus.OnHold)
        {
            // A sibling released it first, or a visitor did. Not an error and not a retry: with
            // several children in flight, only the last one to finish finds a held parent, and this
            // is the branch the other ones take.
            var summary =
                $"Parent work order {parentId} is {parent.CurrentStatus}, not held; nothing to release.";
            _logger.LogInformation(
                "Sub-assembly work order {ChildId} completed; parent {ParentId} is already {Status}. {Summary}",
                completedWorkOrderId, parentId, parent.CurrentStatus, summary);

            return new ParentReleaseResult(ParentReleaseOutcome.NotHeld, summary);
        }

        var from = parent.CurrentStatus;
        var note = child.ForComponentId is { } componentId
            ? $"Sub-assembly order {completedWorkOrderId} delivered {child.PassedQty} × {componentId}; re-picking."
            : $"Sub-assembly order {completedWorkOrderId} completed; re-picking.";

        var released = parent.ReleaseHold(Author, note);
        if (!released.Success)
        {
            _logger.LogWarning(
                "Parent work order {ParentId} could not be released ({Code}): {Error}",
                parentId, released.Code, released.Error);

            return new ParentReleaseResult(ParentReleaseOutcome.NotHeld, released.Error!);
        }

        // The attempt the shortage interrupted, and the quantity still owed — both read off the
        // parent, which is the only place that knows them, and both carried *on the event* so the
        // pick that follows derives nothing from state (6.4's rule, 13.1's application of it).
        // BuildAttempt + 1 is the attempt the failed pick was buying for: 1 before anything is
        // built, N+1 after N attempts have been through inspection.
        var attemptNumber = parent.BuildAttempt + 1;
        var outstanding = parent.OutstandingQty;

        await _eventPublisher.PublishAsync(new WorkOrderScheduled(
            parent.Id,
            parent.OrderedItem.ItemId,
            parent.OrderedItem.ItemName,
            outstanding,
            attemptNumber,
            DateTime.UtcNow), cancellationToken);

        await _workOrders.Update(parent);

        _metrics.Transition(from.ToString(), parent.CurrentStatus.ToString(), parent.Origin.ToString());

        _logger.LogInformation(
            "Parent work order {ParentId} released by sub-assembly order {ChildId} and re-scheduled to pick "
            + "{Outstanding} unit(s) on attempt {Attempt}.",
            parentId, completedWorkOrderId, outstanding, attemptNumber);

        return new ParentReleaseResult(ParentReleaseOutcome.Released,
            $"Parent work order {parentId} released; re-picking attempt {attemptNumber} for {outstanding} unit(s).");
    }
}

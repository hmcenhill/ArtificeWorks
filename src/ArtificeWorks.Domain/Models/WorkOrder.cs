using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Domain.Models;

public class WorkOrder
{
    public Guid Id { get; }
    public WorkOrderStatus CurrentStatus { get; private set; }
    public WorkOrderStatus? PreviousStatus { get; private set; }
    public DateTime CreatedUtc { get; }
    public DateTime UpdatedUtc { get; private set; }

    public Product OrderedItem { get; }
    public uint OrderItemQty { get; }

    /// <summary>
    /// Who asked for this order — a visitor or the simulation (10.3). Set at construction and
    /// immutable: an order does not change who wanted it, and the whole value of the flag is that
    /// it can be trusted by a dashboard filter and a metric dimension.
    /// </summary>
    public WorkOrderOrigin Origin { get; }

    /// <summary>
    /// The serialized units built for this order, across every attempt — including the ones
    /// that were scrapped. Nothing is ever removed: the collection is the record of what this
    /// order actually produced.
    /// </summary>
    public IReadOnlyList<StockKeepingUnit> AssignedStock { get => _assignedStock.AsReadOnly(); }
    private readonly IList<StockKeepingUnit> _assignedStock;

    /// <summary>
    /// How many production attempts this order has had: 0 before anything is built, 1 after
    /// the initial build, 2+ after each rebuild. The rebuild cap counts <em>attempts</em>, not
    /// scrapped units — an attempt that scraps three units has still used one attempt.
    /// </summary>
    public int BuildAttempt { get; private set; }

    /// <summary>Units that have passed inspection — the order's fulfilled quantity.</summary>
    public uint PassedQty => (uint)_assignedStock.Count(unit => unit.Status == UnitStatus.Passed);

    /// <summary>
    /// Units that still count towards the order: passed, plus built-but-not-yet-inspected.
    /// Scrapped units do not, which is precisely why a rebuild has something to do.
    /// </summary>
    public uint LiveQty => (uint)_assignedStock.Count(unit => unit.Status != UnitStatus.Scrapped);

    /// <summary>How many units still need building. A rebuild builds exactly this many.</summary>
    public uint OutstandingQty => OrderItemQty > LiveQty ? OrderItemQty - LiveQty : 0;

    /// <summary>True once the full ordered quantity has passed inspection.</summary>
    public bool IsFulfilled => PassedQty >= OrderItemQty;

    public IList<WorkOrderStateHistory> StateHistory { get; private set; }

    private readonly ICollection<WorkOrderStatus> _cantAdvanceStatuses = new HashSet<WorkOrderStatus>
        {
            WorkOrderStatus.OnHold,
            WorkOrderStatus.Fault
        };

    private readonly ICollection<WorkOrderStatus> _holdStatuses = new HashSet<WorkOrderStatus>
        {
            WorkOrderStatus.OnHold,
            WorkOrderStatus.Fault
        };

    // Business-terminal states: the order is finished and no further lifecycle
    // command may move it. Fault is deliberately NOT terminal — it is a stuck
    // error state, so it stays cancellable as an escape hatch (see Cancel).
    private readonly ICollection<WorkOrderStatus> _terminalStatuses = new HashSet<WorkOrderStatus>
        {
            WorkOrderStatus.Completed,
            WorkOrderStatus.Cancelled
        };

    private WorkOrder() { }

    /// <param name="origin">
    /// Visitor unless the caller says otherwise (10.3) — so every path that existed before the
    /// simulation did keeps producing real demand without being told to.
    /// </param>
    public WorkOrder(
        string createdBy,
        Product item,
        uint qty,
        string? notes = null,
        WorkOrderOrigin origin = WorkOrderOrigin.Visitor)
    {
        if (qty <= 0)
        {
            throw new ArgumentOutOfRangeException("Order Qty must be greater than 0");
        }

        Id = Guid.NewGuid();
        CurrentStatus = WorkOrderStatus.Intake;
        CreatedUtc = DateTime.UtcNow;
        UpdatedUtc = CreatedUtc;
        OrderedItem = item;
        OrderItemQty = qty;
        Origin = origin;
        _assignedStock = new List<StockKeepingUnit>();
        StateHistory = new List<WorkOrderStateHistory>
        {
            new WorkOrderStateHistory(this, createdBy, notes)
        };
    }

    public void SetStatus(WorkOrderStatus newStatus, string createdBy, string? notes = null) // Superuser command
    {
        PreviousStatus = CurrentStatus;
        CurrentStatus = newStatus;
        UpdateStateHistory(createdBy, notes);
    }

    public TransitionResult AdvanceToNextStep(string createdBy, string? notes = null)
    {
        if (_terminalStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Work order is {CurrentStatus} and cannot be advanced further.");
        }
        if (!CanAdvance())
        {
            return TransitionResult.Rejected(TransitionErrorCode.MustReleaseFirst,
                $"Work order cannot be advanced while it is {CurrentStatus}. Release it first.");
        }
        PreviousStatus = CurrentStatus;
        CurrentStatus = GetNextStatus(CurrentStatus);
        UpdateStateHistory(createdBy, notes);

        return TransitionResult.Ok();
    }

    private bool CanAdvance() => !_cantAdvanceStatuses.Contains(CurrentStatus);

    public TransitionResult SetHold(string createdBy, string? notes = null)
    {
        if (_terminalStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Work order is {CurrentStatus} and cannot be held.");
        }
        if (_holdStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.AlreadyHeld,
                $"Work order is already {CurrentStatus} and cannot be held again.");
        }
        PreviousStatus = CurrentStatus;
        CurrentStatus = WorkOrderStatus.OnHold;
        UpdateStateHistory(createdBy, notes);
        return TransitionResult.Ok();
    }

    public TransitionResult ReleaseHold(string createdBy, string? notes = null)
    {
        if (!_holdStatuses.Contains(CurrentStatus) || PreviousStatus is null)
        {
            return TransitionResult.Rejected(TransitionErrorCode.NotHeld,
                $"Work order is {CurrentStatus} and is not currently held; nothing to release.");
        }
        CurrentStatus = PreviousStatus.Value;
        PreviousStatus = null;
        UpdateStateHistory(createdBy, notes);
        return TransitionResult.Ok();
    }

    public TransitionResult Cancel(string createdBy, string? notes = null)
    {
        if (_terminalStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Work order is {CurrentStatus} and cannot be cancelled.");
        }

        // Units built for this order stay with it. Until Epic 6 they were detached here, on
        // the reading that a cancelled order "returns" its stock to a free pool — but this
        // factory builds to order, so there is no pool to return them to, and since 6.1 a
        // unit is owned by exactly one order. Deleting serialized units on cancellation would
        // silently erase the record of what was actually manufactured; keeping them is the
        // honest outcome, and they remain individually addressable for scrap or salvage.

        PreviousStatus = CurrentStatus;
        CurrentStatus = WorkOrderStatus.Cancelled;
        UpdateStateHistory(createdBy, notes);
        return TransitionResult.Ok();
    }

    /// <summary>
    /// Produces the outstanding quantity as serialized units and, on the first attempt, starts
    /// production (Scheduled → InProcess). A rebuild finds the order already InProcess (the
    /// inspection stage put it back there) and simply adds the shortfall.
    /// <para>
    /// The domain verb is <em>build</em>, not "assign": this factory manufactures to order, so
    /// production creates the units rather than picking finished ones off a shelf.
    /// </para>
    /// </summary>
    /// <param name="attemptNumber">Which attempt this is. Must be exactly the next one — see
    /// <see cref="TransitionErrorCode.AttemptOutOfSequence"/>.</param>
    public TransitionResult Build(string createdBy, int attemptNumber, string? notes = null)
    {
        if (_terminalStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Work order is {CurrentStatus} and cannot be built.");
        }
        if (_holdStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.MustReleaseFirst,
                $"Work order cannot be built while it is {CurrentStatus}. Release it first.");
        }
        if (CurrentStatus is not (WorkOrderStatus.Scheduled or WorkOrderStatus.InProcess))
        {
            return TransitionResult.Rejected(TransitionErrorCode.InvalidTransition,
                $"Production can only start from Scheduled or continue from InProcess, not {CurrentStatus}.");
        }
        if (attemptNumber != BuildAttempt + 1)
        {
            // A redelivered rework event asks for an attempt that has already happened. The
            // unique index on the production run is what actually guarantees once-per-attempt;
            // this just catches it before touching the database.
            return TransitionResult.Rejected(TransitionErrorCode.AttemptOutOfSequence,
                $"Work order is on build attempt {BuildAttempt}; attempt {attemptNumber} is out of sequence.");
        }

        var outstanding = OutstandingQty;
        if (outstanding == 0)
        {
            return TransitionResult.Rejected(TransitionErrorCode.InvalidTransition,
                $"Work order already has {LiveQty} of {OrderItemQty} unit(s) accounted for; nothing to build.");
        }

        for (var i = 0; i < outstanding; i++)
        {
            AssignSku(new StockKeepingUnit(OrderedItem, attemptNumber));
        }

        BuildAttempt = attemptNumber;
        if (CurrentStatus == WorkOrderStatus.Scheduled)
        {
            PreviousStatus = CurrentStatus;
            CurrentStatus = WorkOrderStatus.InProcess;
        }
        UpdateStateHistory(createdBy, notes);

        return TransitionResult.Ok();
    }

    /// <summary>
    /// The rework loop's backward step: Inspection → InProcess, so the shortfall left by
    /// scrapped units can be rebuilt. <see cref="AdvanceToNextStep"/> only ever walks forward
    /// via <see cref="GetNextStatus"/>, so this is the one transition that goes back — and it
    /// is deliberately narrow, permitted only from Inspection.
    /// </summary>
    public TransitionResult ReturnToProduction(string createdBy, string? notes = null)
    {
        if (_terminalStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Work order is {CurrentStatus} and cannot be returned to production.");
        }
        if (CurrentStatus != WorkOrderStatus.Inspection)
        {
            return TransitionResult.Rejected(TransitionErrorCode.InvalidTransition,
                $"Only an order in Inspection can be returned to production; this one is {CurrentStatus}.");
        }

        PreviousStatus = CurrentStatus;
        CurrentStatus = WorkOrderStatus.InProcess;
        UpdateStateHistory(createdBy, notes);
        return TransitionResult.Ok();
    }

    /// <summary>
    /// Marks the order as faulted with a reason — "this went wrong and the pipeline has given
    /// up on it". Distinct from <see cref="SetStatus"/> (the unguarded superuser override) so
    /// that a worker routing an order to Fault goes through a real, guarded transition.
    /// <para>
    /// Fault is deliberately not terminal: a faulted order can still be released (back to
    /// whatever it was doing) or cancelled, so this is a stop, not a dead end.
    /// </para>
    /// </summary>
    public TransitionResult Fault(string createdBy, string reason)
    {
        if (_terminalStatuses.Contains(CurrentStatus))
        {
            return TransitionResult.Rejected(TransitionErrorCode.TerminalState,
                $"Work order is {CurrentStatus} and cannot be faulted.");
        }
        if (CurrentStatus == WorkOrderStatus.Fault)
        {
            return TransitionResult.Rejected(TransitionErrorCode.AlreadyHeld,
                "Work order is already faulted.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            return TransitionResult.Rejected(TransitionErrorCode.InvalidTransition,
                "A faulted work order must carry a reason.");
        }

        PreviousStatus = CurrentStatus;
        CurrentStatus = WorkOrderStatus.Fault;
        UpdateStateHistory(createdBy, reason);
        return TransitionResult.Ok();
    }

    /// <summary>Finds one of this order's units by serial number, or null.</summary>
    public StockKeepingUnit? FindUnit(Guid serialNumber) =>
        _assignedStock.FirstOrDefault(unit => unit.SerialNumber == serialNumber);

    /// <summary>The units built on <paramref name="attemptNumber"/> that still await a verdict.</summary>
    public IReadOnlyList<StockKeepingUnit> UnitsAwaitingInspection(int attemptNumber) =>
        _assignedStock
            .Where(unit => unit.BuildAttempt == attemptNumber && unit.Status == UnitStatus.Built)
            .ToList();

    /// <summary>
    /// Records an annotation against the order at its <em>current</em> status without
    /// changing state — an audit "touch". Used by async consumers to leave a trace that
    /// they processed an event (e.g. the Epic 4.2 worker acknowledging a scheduling
    /// event) so the effect of a consumed message is observable in the state history.
    /// </summary>
    public void AppendNote(string recordedBy, string note) => UpdateStateHistory(recordedBy, note);

    private void UpdateStateHistory(string createdBy, string? notes = null)
    {
        UpdatedUtc = DateTime.UtcNow;
        StateHistory.Add(new WorkOrderStateHistory(this, createdBy, notes));
    }

    private static WorkOrderStatus GetNextStatus(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Intake => WorkOrderStatus.Scheduled,
        WorkOrderStatus.Scheduled => WorkOrderStatus.InProcess,
        WorkOrderStatus.InProcess => WorkOrderStatus.Inspection,
        WorkOrderStatus.Inspection => WorkOrderStatus.Delivery,
        WorkOrderStatus.Delivery => WorkOrderStatus.Completed,
        _ => WorkOrderStatus.Fault,
    };

    /// <summary>
    /// Attaches a serialized unit to the order. This is the mechanism <see cref="Build"/> uses;
    /// the domain verb callers should reach for is "build".
    /// <para>
    /// The fulfilment guard counts <see cref="LiveQty"/>, not the raw collection size: once
    /// rebuilds exist, an order of 5 that scrapped 2 units holds 7 rows but is still 2 short,
    /// and counting all of them would refuse to rebuild the shortfall.
    /// </para>
    /// </summary>
    public bool AssignSku(StockKeepingUnit unit)
    {
        if (!OrderedItem.ItemId.Equals(unit.Product.ItemId) || LiveQty >= OrderItemQty)
        {
            // Wrong Product or qty already fulfilled
            return false;
        }

        _assignedStock.Add(unit);
        return true;
    }

    public bool UnassignSku(Guid serialNumber)
    {
        var unit = _assignedStock.FirstOrDefault(sku => sku.SerialNumber == serialNumber);
        if (unit is not null)
        {
            _assignedStock.Remove(unit);
            return true;
        }
        return false;
    }
}

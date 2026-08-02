using ArtificeWorks.Application.Data;
using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.Interfaces;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> Get(Guid id);
    Task<WorkOrder?> GetWithHistory(Guid id);
    Task<WorkOrder> Add(WorkOrder workOrder);
    Task Update(WorkOrder workOrder);

    /// <summary>
    /// The board's list read model (11.1): a slim <see cref="WorkOrderListItemDto"/> per order,
    /// projected in the database, newest-first and bounded by <paramref name="limit"/>.
    /// <para>
    /// With no <paramref name="statuses"/> filter the result is the bounded live world — every
    /// in-flight order plus a capped window of the most-recently-terminal ones (Completed /
    /// Cancelled), the terminal ones being the first dropped when the limit is reached. This
    /// mirrors 10.4's sweep rather than an ever-growing wall of finished orders. An explicit
    /// status filter turns that off and simply returns the matching orders newest-first.
    /// </para>
    /// </summary>
    /// <param name="statuses">Statuses to include; empty means "the bounded live world" (see above).</param>
    /// <param name="origins">Origins to include; empty means both.</param>
    /// <param name="limit">Maximum rows to return. The caller is expected to have clamped it.</param>
    Task<IReadOnlyList<WorkOrderListItemDto>> List(
        IReadOnlyCollection<WorkOrderStatus> statuses,
        IReadOnlyCollection<WorkOrderOrigin> origins,
        int limit);

    // ------------------------------------------------------------ 13.3: sub-assembly work orders

    /// <summary>
    /// The components for which this parent already has an <em>unfinished</em> child order on the
    /// given pick attempt. The cheap pre-check that stops a redelivery from even trying to spawn a
    /// second one; as everywhere else in this system, the filtered unique index is the guarantee.
    /// <para>
    /// <strong>There is deliberately no "add a child" method beside it.</strong> A child is attached
    /// to its parent's <c>Children</c> collection by <see cref="WorkOrder.ForSubAssembly"/>, so it is
    /// already in the caller's tracked graph and <see cref="Update"/> commits it alongside the
    /// parent's hold and the outbox rows announcing it. A dedicated insert would be a second write
    /// that could drift from the hold it belongs with.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> ListOpenSubAssemblyRequests(
        Guid parentWorkOrderId,
        int parentAttemptNumber,
        CancellationToken cancellationToken = default);
}

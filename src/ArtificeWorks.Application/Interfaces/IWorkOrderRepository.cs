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
}

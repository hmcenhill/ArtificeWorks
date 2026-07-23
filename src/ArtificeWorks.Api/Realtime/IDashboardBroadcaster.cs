using Microsoft.AspNetCore.SignalR;

namespace ArtificeWorks.Api.Realtime;

/// <summary>
/// The seam between the broker relay and SignalR. The <see cref="DashboardRelay"/> depends on this
/// rather than on <see cref="IHubContext{THub,T}"/> directly, so its ack-always behaviour can be
/// tested with a broadcaster that throws — proving a dropped frame still acks and never requeues.
/// </summary>
public interface IDashboardBroadcaster
{
    Task BroadcastAsync(DashboardEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>Broadcasts to every connected browser through the <see cref="DashboardHub"/>.</summary>
public sealed class HubDashboardBroadcaster : IDashboardBroadcaster
{
    private readonly IHubContext<DashboardHub, IDashboardClient> _hub;

    public HubDashboardBroadcaster(IHubContext<DashboardHub, IDashboardClient> hub) => _hub = hub;

    public Task BroadcastAsync(DashboardEvent @event, CancellationToken cancellationToken = default)
        => _hub.Clients.All.WorkOrderEvent(@event);
}

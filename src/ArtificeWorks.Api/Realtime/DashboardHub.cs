using Microsoft.AspNetCore.SignalR;

namespace ArtificeWorks.Api.Realtime;

/// <summary>
/// The one method a connected browser receives: a <see cref="DashboardEvent"/> for every
/// <c>work-order.*</c> event the factory publishes. Strongly typed so the server can't push a
/// method the client doesn't know, and the client (TS or the .NET test) binds to the same name.
/// </summary>
public interface IDashboardClient
{
    Task WorkOrderEvent(DashboardEvent @event);
}

/// <summary>
/// The dashboard's realtime endpoint. Clients only <em>receive</em> — there are no server methods
/// to invoke — so the hub itself is empty: a visitor action is an ordinary pipeline HTTP call
/// (the epic's "no dashboard back door"), not a hub message. The <see cref="DashboardRelay"/>
/// broadcasts through it via <see cref="IDashboardBroadcaster"/>.
/// </summary>
public sealed class DashboardHub : Hub<IDashboardClient>
{
    /// <summary>Where the hub is mapped; the dev proxy passes <c>/hubs</c> through with websockets.</summary>
    public const string Route = "/hubs/dashboard";
}

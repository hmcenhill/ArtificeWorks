namespace ArtificeWorks.Application.Messaging.Events;

/// <summary>
/// The last event in the chain: the work order is Completed. For a customer's order that means the
/// parcel is with a carrier; for a sub-assembly order (13.3) it means the units have been put away
/// as component stock.
/// <para>
/// <strong>One event, not two.</strong> A <c>shipment-dispatched</c> alongside this would say the
/// same thing at the same instant, because completion is automatic once a shipment is dispatched
/// — the visitor's decision is <em>which carrier</em>, not <em>whether to finish</em>. And 13.3
/// reuses this key rather than minting a <c>sub-assembly-stocked</c>: the announcement a child
/// order has finished is the announcement the whole system already understands, which is what lets
/// the relay, the board and the timeline treat a child exactly as they treat any other order.
/// </para>
/// <para>
/// Since 13.3 it has its <strong>first pipeline subscriber</strong> — the handler that releases a
/// parent when its child finishes. It remains an announcement for a top-level order: nothing in
/// the pipeline acts on one completing, and the dashboard relay reads it for the feed.
/// </para>
/// </summary>
/// <param name="Carrier">
/// Who is carrying the parcel — <c>null</c> for a sub-assembly order, which is stocked rather than
/// shipped. Booking a fictional carrier to move parts from one end of the factory to the other
/// would be a lie told to avoid one nullable field.
/// </param>
/// <param name="TrackingNumber">The parcel's number, <c>null</c> for the same reason.</param>
/// <param name="SerialNumbers">The units that went out, or that were put away.</param>
/// <param name="ForComponentId">
/// The component a sub-assembly order's output was credited to, and the quickest way for a
/// subscriber to tell "this was stock" from "this was a parcel". <c>null</c> for a customer's order.
/// </param>
public sealed record WorkOrderCompleted(
    Guid WorkOrderId,
    string ProductId,
    string? Carrier,
    string? TrackingNumber,
    IReadOnlyList<Guid> SerialNumbers,
    DateTime CompletedUtc,
    string? ForComponentId = null) : IntegrationEvent
{
    public override string EventType => "work-order.completed";
}

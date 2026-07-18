namespace ArtificeWorks.Application.Messaging;

/// <summary>
/// The correlation id for the current logical operation — one API request, or (from
/// Epic 4.2) one message being handled. Established at the API boundary and stamped onto
/// every event published during the operation. Story 4.3 flows it into log scopes on
/// both services so grepping one id tells a work order's whole story end to end.
/// </summary>
public interface ICorrelationContext
{
    Guid CorrelationId { get; }
}

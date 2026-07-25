using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Chaos;

namespace ArtificeWorks.Api.Controllers;

/// <param name="WorkOrderId">The one order this fault targets.</param>
/// <param name="Kind">Which fault to arm. Defaults to the only one 12.1 fires; the broker faults are 12.2.</param>
public sealed record ChaosRequest(Guid WorkOrderId, InjectedFaultKind Kind = InjectedFaultKind.FailInspection);

/// <summary>What was armed, and against which order — echoed back so a caller (and 12.3's UI) can confirm the target.</summary>
public sealed record ChaosArmedDto(Guid FaultId, Guid WorkOrderId, string Kind, DateTime ArmedUtc, string ArmedBy)
{
    public static ChaosArmedDto From(InjectedFault fault) =>
        new(fault.Id, fault.WorkOrderId, fault.Kind.ToString(), fault.ArmedUtc, fault.ArmedBy);
}

/// <summary>
/// The visitor's lever: arm one order to fail on demand (Epic 12).
/// <para>
/// <strong>Per-order, the deliberate opposite of 10.2's dials.</strong> The dials retune the whole
/// factory; a fault here names one <c>WorkOrderId</c> and touches nothing else on the floor. That
/// bounded blast radius, the rate limiter below, and 10.4's world-reset sweep are together the
/// "cannot corrupt the shared world" and "cannot grief each other" guarantees.
/// </para>
/// <para>
/// <strong>Why <c>/system</c>, and no auth yet.</strong> Same reasoning as the dials, the dead
/// letters and the world reset: the admin gate deferred since Epic 3 should fall on one path prefix,
/// and a stranger arming faults is exactly what it will one day guard. Until it exists this is
/// unauthenticated like its neighbours — recorded here rather than forgotten, and the reason the
/// rate limiter is not optional.
/// </para>
/// </summary>
[ApiController]
[Route("system/chaos")]
[Produces("application/json")]
[EnableRateLimiting(RateLimitPolicy)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public class ChaosController(ChaosService chaos) : ApiControllerBase
{
    /// <summary>The rate-limiter policy name, registered against this endpoint in <c>Program.cs</c>.</summary>
    public const string RateLimitPolicy = "chaos";

    /// <summary>
    /// Arms a fault against one order. Returns what was armed; refuses an order that does not exist
    /// (<c>chaos_target_not_found</c>, 404) or cannot be broken — terminal, faulted, or past the
    /// stage this fault can touch (<c>chaos_target_not_injectable</c>, 409).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ChaosArmedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ChaosArmedDto>> Arm(
        [FromBody] ChaosRequest request,
        [FromQuery] string armedBy = "visitor",
        CancellationToken cancellationToken = default)
    {
        var result = await chaos.Arm(request.WorkOrderId, request.Kind, armedBy, cancellationToken);

        return result.Outcome switch
        {
            ChaosArmOutcome.Armed => Ok(ChaosArmedDto.From(result.Fault!)),
            ChaosArmOutcome.TargetNotFound => Problem(
                StatusCodes.Status404NotFound, ProblemCodes.ChaosTargetNotFound, result.Summary),
            _ => Problem(
                StatusCodes.Status409Conflict, ProblemCodes.ChaosTargetNotInjectable, result.Summary),
        };
    }
}

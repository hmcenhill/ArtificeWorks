using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Application.Simulation;

namespace ArtificeWorks.Api.Controllers;

/// <summary>
/// Put the factory floor back (10.4).
/// <para>
/// <strong>This runs exactly the code the schedule runs.</strong> There is no second path to keep
/// in step, which is the story's first acceptance criterion — and it means "reset the world" is
/// something a visitor can do before a demo without anyone having to run
/// <c>docker compose down -v</c>.
/// </para>
/// <para>
/// <strong>It is a sweep, not a truncate.</strong> Component stock returns to seed levels and old
/// terminal, held or faulted orders are retired. Anything in flight, the catalog, and the dead
/// letters Epic 12 exists to display are never touched — which is what makes it safe to press at
/// any moment.
/// </para>
/// <para>
/// Under <c>/system</c> with 8.3's dead letters and 10.2's dials, behind the same eventual admin
/// gate: a visitor must not be able to clear the board before a demo someone else is watching.
/// Until that gate exists this is unauthenticated, like its neighbours, and that is recorded here
/// rather than forgotten.
/// </para>
/// </summary>
[ApiController]
[Route("system/world")]
[Produces("application/json")]
public class WorldController(WorldResetService world) : ApiControllerBase
{
    [HttpPost("reset")]
    [ProducesResponseType<WorldResetResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<WorldResetResult>> Reset(
        [FromQuery] string triggeredBy = "api",
        CancellationToken cancellationToken = default)
        => Ok(await world.Sweep(triggeredBy, cancellationToken));
}

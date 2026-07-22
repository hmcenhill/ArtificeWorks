using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Observability;

namespace ArtificeWorks.Api.Controllers;

/// <summary>
/// The factory's vital signs as plain JSON (9.2).
/// <para>
/// <strong>Why this exists next to a metrics backend.</strong> Instrument once with
/// <c>System.Diagnostics.Metrics</c> for Grafana; expose a cheap mirror for Epic 11. The
/// alternative — a React dashboard querying Prometheus — pushes a query language, a CORS problem
/// and an auth problem into the frontend epic to save an endpoint that is a few dozen lines over
/// data that already exists. It also answers with a metrics backend switched off entirely, which
/// matters for a demo that has to survive one container being down.
/// </para>
/// <para>
/// Served from the <em>same</em> cached snapshot the observable gauges read, so Grafana and this
/// endpoint can never disagree — and no request here issues a database query.
/// </para>
/// <para>
/// Under <c>/system</c> with 8.3's dead letters, so the admin gate that has been deferred since
/// then still falls on one path prefix rather than a scattering of endpoints.
/// </para>
/// </summary>
[ApiController]
[Route("system/stats")]
[Produces("application/json")]
public class SystemStatsController(PipelineSnapshotCache snapshot, ArtificeWorksMetrics metrics) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<SystemStatsDto>(StatusCodes.Status200OK)]
    public ActionResult<SystemStatsDto> Get() => Ok(SystemStatsDto.From(snapshot.Current, metrics));
}

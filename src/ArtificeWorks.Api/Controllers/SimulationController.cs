using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Simulation;
using ArtificeWorks.Infrastructure.Messaging;
using ArtificeWorks.Infrastructure.Simulation;

namespace ArtificeWorks.Api.Controllers;

/// <summary>
/// The factory's dials, turnable while it runs (10.2).
/// <para>
/// <strong>Why <c>/system</c>.</strong> Same reasoning as 8.3's dead letters and 10.4's world
/// reset: the admin gate deferred since Epic 3 should land on <em>one path prefix</em> rather than a
/// scattering of endpoints — and a stranger with a mouse being able to set the failure rate to 1.0
/// is precisely what that gate is for. <strong>There is no auth yet</strong>, which is deliberate
/// while the whole demo is people pressing buttons, and recorded rather than forgotten.
/// </para>
/// <para>
/// <strong>A change is not instant, and the response says so.</strong> Every host reads these
/// through a cached snapshot refreshed on a timer, so the worker — where inspections actually fail
/// — picks a change up on its next tick. Making it instant means a query per decision or a
/// broadcast, and both are worse than a few seconds' lag on a demo dial.
/// </para>
/// </summary>
[ApiController]
[Route("system/simulation")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public class SimulationController(
    SimulationSettingsService settings,
    IOptions<PaceConfiguration> pace,
    IOptions<SimulationConfiguration> simulation) : ApiControllerBase
{
    private readonly SimulationSettingsService _settings = settings;
    private readonly PaceConfiguration _pace = pace.Value;
    private readonly SimulationConfiguration _simulation = simulation.Value;

    /// <summary>
    /// What the factory is running on right now, where it came from, and — when pacing is on —
    /// which rung each stage's duration actually resolved to.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<SimulationSettingsDto>(StatusCodes.Status200OK)]
    public ActionResult<SimulationSettingsDto> Get() => Ok(Describe(_settings.Current));

    /// <summary>
    /// Replaces the settings. Out-of-range values are refused with <c>422</c> and code
    /// <c>simulation_setting_out_of_range</c>, and change nothing — a rejected <c>PUT</c> must
    /// leave the live factory exactly as it was.
    /// </summary>
    [HttpPut]
    [ProducesResponseType<SimulationSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SimulationSettingsDto>> Put(
        [FromBody] SimulationSettingsDto request,
        [FromQuery] string updatedBy = "api",
        CancellationToken cancellationToken = default)
    {
        var takesEffect = (int)Math.Ceiling(_simulation.SettingsRefreshMs / 1000.0);

        var result = await _settings.Update(
            request.ToSettings(), updatedBy, takesEffect, cancellationToken);

        return result.Outcome switch
        {
            SimulationSettingsOutcome.Applied => Ok(Describe(result.Settings, takesEffect)),
            _ => Problem(StatusCodes.Status422UnprocessableEntity,
                ProblemCodes.SimulationSettingOutOfRange, result.Summary)
        };
    }

    private SimulationSettingsDto Describe(SimulationSettings current, int takesEffectSeconds = 0) =>
        SimulationSettingsDto.From(current, _settings.IsOverridden, ResolvedRungs(current), takesEffectSeconds);

    /// <summary>
    /// The rung each stage's configured duration snaps to. Computed without jitter, because this is
    /// "where does 5 seconds land?" rather than "what will the next message do?".
    /// </summary>
    private IReadOnlyDictionary<string, string>? ResolvedRungs(SimulationSettings current)
    {
        if (!current.PacingEnabled)
        {
            return null;
        }

        var rungs = new Dictionary<string, string>();

        foreach (var eventType in new[]
        {
            "work-order.scheduled",
            "work-order.materials-reserved",
            "work-order.production-completed",
            "work-order.rework-required",
            "work-order.inspection-passed",
            "work-order.shipment-scheduled",
        })
        {
            rungs[eventType] = _pace.RungFor(current.PaceFor(eventType)) is int rung
                ? _pace.LabelFor(rung)
                : "none";
        }

        return rungs;
    }
}

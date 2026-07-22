using Microsoft.AspNetCore.Mvc;

using ArtificeWorks.Api.Errors;
using ArtificeWorks.Application.Data;
using ArtificeWorks.Application.Recovery;

namespace ArtificeWorks.Api.Controllers;

/// <summary>
/// Inspect what failed, and put it back (8.3).
/// <para>
/// <strong>Why <c>/system</c> and not <c>/dead-letters</c>.</strong> Everything under
/// <c>/work-orders</c> is about the factory; this is about the machinery underneath it, and it
/// belongs behind the same admin gate <c>SetStatus</c> has been waiting on since Epic 3. Putting
/// it under a path prefix now means that gate can fall on one route rather than on a scattering
/// of endpoints. <strong>There is no auth yet</strong> — and in the meantime that is deliberate,
/// because the whole demo is people pressing recovery buttons.
/// </para>
/// </summary>
[ApiController]
[Route("system/dead-letters")]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
public class DeadLetterController(DeadLetterService deadLetters) : ApiControllerBase
{
    private readonly DeadLetterService _deadLetters = deadLetters;

    /// <summary>
    /// What has failed, newest first. Filter by <c>workOrderId</c> to see one order's failures, or
    /// by <c>replayed</c> to separate what has been dealt with from what is still waiting.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<DeadLetterPageDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeadLetterPageDto>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] Guid? workOrderId = null,
        [FromQuery] bool? replayed = null,
        CancellationToken cancellationToken = default)
        => Ok(await _deadLetters.List(page, pageSize, workOrderId, replayed, cancellationToken));

    /// <summary>The full record, payload included — what a human reads before deciding to replay.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<DeadLetterDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeadLetterDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var letter = await _deadLetters.Get(id, cancellationToken);
        return letter is null
            ? Problem(StatusCodes.Status404NotFound, ProblemCodes.DeadLetterNotFound,
                $"No dead letter found with id: {id}")
            : Ok(letter);
    }

    /// <summary>
    /// Republishes the message under its original routing key, via the outbox, with the retry
    /// ladder reset to attempt 1.
    /// <para>
    /// Replaying something that has already been replayed returns 409
    /// <c>dead_letter_already_replayed</c> unless <c>force=true</c>. That is not a safety
    /// interlock — the dedupe keys from 5.4, 6.4 and 7.1 make a replayed message land on an
    /// already-done stage and skip, exactly as a redelivery does — it is an answer to the
    /// question a second click is usually asking, which is "did the first one work?".
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/replay")]
    [ProducesResponseType<ReplayResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReplayResult>> Replay(
        Guid id,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _deadLetters.Replay(id, force, cancellationToken);

        return result.Outcome switch
        {
            ReplayOutcome.Replayed => Accepted(result),
            ReplayOutcome.NotFound
                => Problem(StatusCodes.Status404NotFound, ProblemCodes.DeadLetterNotFound, result.Summary),
            _ => Problem(StatusCodes.Status409Conflict, ProblemCodes.DeadLetterAlreadyReplayed, result.Summary)
        };
    }
}

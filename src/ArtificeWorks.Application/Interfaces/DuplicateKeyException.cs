namespace ArtificeWorks.Application.Interfaces;

/// <summary>
/// A unique constraint was violated on a write the caller is expected to resolve rather than
/// report. Infrastructure translates the provider-specific violation into this so Application
/// code can react to it without referencing EF Core or Npgsql.
/// <para>
/// The one case today (8.4): two simultaneous requests carrying the same
/// <c>Idempotency-Key</c>. The loser must not become a 500 — the filter that owns the key
/// contract replays the winner's response — so this deliberately escapes the create handler's
/// catch-all.
/// </para>
/// <para>
/// Note the contrast with the dedupe keys Epics 5–7 established. Those are caught <em>at the
/// repository</em> and turned into a "someone else did it" result, because the caller genuinely
/// has nothing left to do. This one has to travel, because the answer to it is a response body
/// only the edge knows how to build.
/// </para>
/// </summary>
public sealed class DuplicateKeyException : Exception
{
    public DuplicateKeyException(string message, Exception inner) : base(message, inner)
    {
    }
}

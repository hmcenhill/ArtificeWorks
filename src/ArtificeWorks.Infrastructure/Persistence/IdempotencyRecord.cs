namespace ArtificeWorks.Infrastructure.Persistence;

/// <summary>
/// A client-supplied <c>Idempotency-Key</c> and the response it earned (8.4).
/// <para>
/// <strong>The response is stored, not just the key.</strong> A bare "already seen" marker could
/// only answer a retry with a 409, and a client that never received the first response would have
/// no way to learn its order's id. Replaying the original <c>201</c> is the only answer that
/// leaves the caller correct.
/// </para>
/// <para>
/// <strong>The row is staged before the action runs and committed by the action's own
/// <c>SaveChanges</c></strong> — the marker and the work commit together, 5.4's rule applied to
/// the one edge the system doesn't control. A row with no response yet is therefore an
/// <em>in-flight</em> request, not a finished one, and is answered as such.
/// </para>
/// </summary>
public class IdempotencyRecord
{
    private IdempotencyRecord()
    {
        Key = null!;
        Endpoint = null!;
        RequestHash = null!;
    }

    public IdempotencyRecord(string key, string endpoint, string requestHash, DateTime createdUtc)
    {
        Id = Guid.NewGuid();
        Key = key;
        Endpoint = endpoint;
        RequestHash = requestHash;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The client's key. Unique — and the uniqueness <em>is</em> the guarantee, not the pre-check read.</summary>
    public string Key { get; private set; }

    public string Endpoint { get; private set; }

    /// <summary>
    /// A hash of the request body. Same key with a different body is a client bug; silently
    /// replaying the first response would hide it.
    /// </summary>
    public string RequestHash { get; private set; }

    public int? StatusCode { get; private set; }

    public string? ResponseBody { get; private set; }

    public string? ResponseLocation { get; private set; }

    public DateTime CreatedUtc { get; private set; }

    public DateTime? CompletedUtc { get; private set; }

    /// <summary>True once the original response has been captured and can be replayed verbatim.</summary>
    public bool IsComplete => StatusCode is not null;

    public void RecordResponse(int statusCode, string? body, string? location, DateTime completedUtc)
    {
        StatusCode = statusCode;
        ResponseBody = body;
        ResponseLocation = location;
        CompletedUtc = completedUtc;
    }
}

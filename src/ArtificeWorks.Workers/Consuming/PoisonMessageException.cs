namespace ArtificeWorks.Workers.Consuming;

/// <summary>
/// A message that cannot be handled <em>and never will be</em>: a body that won't deserialize, a
/// routing key with no handler behind it. Retrying it would be the same failure five times.
/// <para>
/// The distinction this type draws is the whole of 8.2's classification. Everything else that
/// throws is assumed <strong>transient</strong> — a database blip, a broker hiccup, a
/// <c>DbUpdateConcurrencyException</c> from 8.1's new token — and gets the retry ladder. Only
/// what is provably permanent skips it and parks immediately.
/// </para>
/// </summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException(string message) : base(message)
    {
    }

    public PoisonMessageException(string message, Exception inner) : base(message, inner)
    {
    }
}

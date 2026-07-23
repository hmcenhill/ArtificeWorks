namespace ArtificeWorks.Domain.Models;

/// <summary>
/// Who asked for this work order (10.3).
/// <para>
/// <strong>A persisted column, not a <c>CreatedBy</c> string convention.</strong> Once the
/// simulation creates orders on its own, "is that mine?" becomes a question the dashboard has to
/// answer and <c>/system/stats</c> counting robot traffic as demand becomes a lie. A convention
/// costs no migration and is unqueryable, unindexable, and one typo away from silently
/// mis-classifying a visitor's order; <c>LIKE 'sim:%'</c> over an author string is not a query
/// anyone should have to write. Epic 11 filters a board on this, Epic 12 scopes chaos with it, and
/// Epic 15 wants simulated traffic excluded from anything that looks like a business number.
/// </para>
/// <para>
/// Two values, forever, which is what makes it safe as a metric dimension as well — 9.2's rule is
/// that a metric never gets a <em>per-order</em> label, and a two-valued one is exactly the kind it
/// does get.
/// </para>
/// </summary>
public enum WorkOrderOrigin
{
    /// <summary>A human asked for it. The default, so anything that does not say otherwise is real demand.</summary>
    Visitor = 0,

    /// <summary>The simulation's order generator asked for it.</summary>
    Simulated = 1
}

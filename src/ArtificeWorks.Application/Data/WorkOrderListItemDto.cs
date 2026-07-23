using System.Text.Json.Serialization;

using ArtificeWorks.Domain.Models;

namespace ArtificeWorks.Application.Data;

/// <summary>
/// The board's read model (11.1): one lightweight row per order, enough to place a card in a
/// stage column and tell a visitor's order from a robot's — and no more.
/// <para>
/// Deliberately <em>not</em> <see cref="WorkOrderDto"/>: a board renders a card, not a
/// unit-by-unit breakdown, so it carries six fields rather than the heavy graph of units, the
/// shipment and the history. The detail view pays for the heavy read on click.
/// </para>
/// <para>
/// <see cref="Status"/> and <see cref="Origin"/> cross the wire as their <em>names</em>, not their
/// ordinals — the board keys columns and badges off them, and a value that shifts when the enum is
/// reordered is a silent break. The rest of the API still emits numeric enums; this is scoped to
/// the one DTO a TypeScript client mirrors, exactly as <see cref="WorkOrderTimelineDto"/>'s
/// <c>kind</c> is a string for the same reason.
/// </para>
/// </summary>
public sealed record WorkOrderListItemDto(
    Guid Id,
    string ProductName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] WorkOrderStatus Status,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] WorkOrderOrigin Origin,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

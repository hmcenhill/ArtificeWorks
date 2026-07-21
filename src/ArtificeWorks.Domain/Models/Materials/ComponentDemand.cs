namespace ArtificeWorks.Domain.Models.Materials;

/// <summary>
/// How much of one component a work order needs: <c>QtyPerUnit × OrderItemQty</c>, resolved
/// from a product's BOM. The unit of currency between "what the BOM says" and "what the
/// reservation must take off the shelf".
/// </summary>
public sealed record ComponentDemand(string ComponentId, string ComponentName, uint Quantity);

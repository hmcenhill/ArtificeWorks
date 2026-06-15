namespace OrderProcessing.Application.Commands;

public class CreateWorkOrderRequest
{
    public required string Requestor { get; set; }
    public required string ItemId { get; set; }
    public uint Qty { get; set; }
    public string? Notes { get; set; }
}

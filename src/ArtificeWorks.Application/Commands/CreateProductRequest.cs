namespace ArtificeWorks.Application.Commands;

public class CreateProductRequest
{
    public required string Requestor { get; set; }
    public required string ProductId { get; set; }
    public required string ProductName { get; set; }
}
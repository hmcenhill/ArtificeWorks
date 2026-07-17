using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.UnitTests;

internal static class TestData
{
    public static Product DefaultProduct() => new Product("TEST-001", "Test Product");
    public static Product SomeOtherProduct() => new Product("TEST-002", "Different Product");
}
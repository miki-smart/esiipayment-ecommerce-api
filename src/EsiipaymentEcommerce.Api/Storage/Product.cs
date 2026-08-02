namespace EsiipaymentEcommerce.Api.Storage;

public sealed class Product
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required long PriceMinorUnits { get; init; }
    public required string Currency { get; init; }
}

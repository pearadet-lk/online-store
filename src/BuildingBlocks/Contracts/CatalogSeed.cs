namespace Contracts;

/// <summary>
/// Deterministic demo catalog shared by product-service (PostgreSQL seed + in-memory fallback)
/// and inventory-service (in-memory stock rows).
/// </summary>
public static class CatalogSeed
{
    public const int DefaultProductCount = 100;

    private static readonly IReadOnlyList<ProductDto> Products = BuildProducts();

    public static IReadOnlyList<ProductDto> DefaultProducts() => Products;

    private static IReadOnlyList<ProductDto> BuildProducts()
    {
        var list = new List<ProductDto>(DefaultProductCount);

        list.Add(new ProductDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Starter Keyboard",
            "Entry-level keyboard",
            39.99m,
            true));

        list.Add(new ProductDto(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Gaming Mouse",
            "RGB gaming mouse",
            59.99m,
            true));

        ReadOnlySpan<string> departments =
        [
            "Electronics", "Home", "Sports", "Office", "Garden", "Books", "Music", "Toys"
        ];

        ReadOnlySpan<string> kinds =
        [
            "Adapter", "Cable", "Stand", "Kit", "Pack", "Set", "Mat", "Lamp", "Holder", "Case"
        ];

        for (var i = 3; i <= DefaultProductCount; i++)
        {
            var id = new Guid($"00000000-0000-4000-8000-{i:X12}");
            var dept = departments[(i - 3) % departments.Length];
            var kind = kinds[(i * 7) % kinds.Length];
            var name = $"{dept} {kind} {i:D3}";
            var description = $"Seeded demo product {i} for catalog browsing and checkout testing.";
            var price = decimal.Round(4.99m + (i * 37 % 450) * 0.15m, 2, MidpointRounding.AwayFromZero);
            list.Add(new ProductDto(id, name, description, price, true));
        }

        return list;
    }
}

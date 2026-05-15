namespace ProductService;

internal sealed class CatalogOptions(string? connectionString)
{
    public string? ConnectionString { get; set; } = connectionString;
}

namespace ProductService;

internal interface IProductRepositoryResolver
{
    IProductRepository Repository { get; }
    PostgresProductRepository? PostgresRepository { get; }
    void DisablePostgres();
}

internal sealed class ProductRepositoryResolver(CatalogOptions options) : IProductRepositoryResolver
{
    private readonly InMemoryProductRepository _inMemoryRepository = new();
    private PostgresProductRepository? _postgresRepository = string.IsNullOrWhiteSpace(options.ConnectionString)
        ? null
        : new PostgresProductRepository(options.ConnectionString);

    public IProductRepository Repository => _postgresRepository is not null
        ? _postgresRepository
        : _inMemoryRepository;
    public PostgresProductRepository? PostgresRepository => _postgresRepository;

    public void DisablePostgres()
    {
        _postgresRepository = null;
    }
}

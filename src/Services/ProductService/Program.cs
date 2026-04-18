using System.Collections.Concurrent;
using Contracts;
using ProductService;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Services.AddOpenApi();
    builder.Services.AddSingleton<ProductStore>();
    builder.Services.AddSingleton(_ => new CatalogOptions(builder.Configuration.GetConnectionString("Catalog")));

    var app = builder.Build();

    var catalogConn = app.Services.GetRequiredService<CatalogOptions>().ConnectionString;
    if (!string.IsNullOrWhiteSpace(catalogConn))
    {
        try
        {
            await PostgresCatalog.EnsureSchemaAndSeedAsync(catalogConn, CancellationToken.None);
            app.Logger.LogInformation("Product catalog synchronized with PostgreSQL.");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "PostgreSQL unavailable; falling back to in-memory catalog.");
            app.Services.GetRequiredService<CatalogOptions>().ConnectionString = null;
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseSerilogRequestLogging();

    app.MapGet("/health", (CatalogOptions opts) =>
        Results.Ok(new
        {
            service = "product-service",
            status = "ok",
            dataSource = string.IsNullOrWhiteSpace(opts.ConnectionString) ? "memory" : "postgresql"
        }));

    app.MapGet("/products", async (string? q, ProductStore store, CatalogOptions opts, CancellationToken ct) =>
    {
        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            var list = await PostgresCatalog.ListAsync(opts.ConnectionString, q, ct);
            return Results.Ok(list);
        }

        var products = store.Products.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            products = products.Where(x =>
                x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return Results.Ok(products.OrderBy(x => x.Name));
    });

    app.MapGet("/products/{productId:guid}", async (Guid productId, ProductStore store, CatalogOptions opts, CancellationToken ct) =>
    {
        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            var list = await PostgresCatalog.ListAsync(opts.ConnectionString, null, ct);
            var match = list.FirstOrDefault(x => x.ProductId == productId);
            return match is null ? Results.NotFound() : Results.Ok(match);
        }

        return store.Products.TryGetValue(productId, out var product)
            ? Results.Ok(product)
            : Results.NotFound();
    });

    app.MapPost("/products", (ProductDto request, ProductStore store, CatalogOptions opts) =>
    {
        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var product = request with
        {
            ProductId = request.ProductId == Guid.Empty ? Guid.NewGuid() : request.ProductId
        };
        store.Products[product.ProductId] = product;
        return Results.Created($"/products/{product.ProductId}", product);
    });

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Product service host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

internal sealed class CatalogOptions(string? connectionString)
{
    public string? ConnectionString { get; set; } = connectionString;
}

internal sealed class ProductStore
{
    public ConcurrentDictionary<Guid, ProductDto> Products { get; } = new(
        new[]
        {
            new KeyValuePair<Guid, ProductDto>(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                new ProductDto(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Starter Keyboard", "Entry-level keyboard", 39.99m, true)),
            new KeyValuePair<Guid, ProductDto>(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                new ProductDto(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Gaming Mouse", "RGB gaming mouse", 59.99m, true))
        });
}

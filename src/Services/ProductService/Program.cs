using System.Collections.Concurrent;
using System.Diagnostics;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using ProductService;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Shared;

const string ServiceName = "product-service";
const string DefaultApiVersion = "v1";

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
    {
        var elasticsearchUrl = context.Configuration["Observability:ElasticsearchUrl"] ?? "http://elasticsearch:9200";
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", ServiceName)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [TraceId:{TraceId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticsearchUrl))
            {
                AutoRegisterTemplate = true,
                IndexFormat = $"online-store-{ServiceName}-logs-{DateTime.UtcNow:yyyy.MM}"
            });
    });
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(ServiceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(builder.Configuration["Observability:OtlpEndpoint"] ?? "http://jaeger:4317");
            }));
    builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseGlobalExceptionHandling(ServiceName);

    app.Use(async (context, next) =>
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        context.Response.Headers["X-Trace-Id"] = traceId;
        using (LogContext.PushProperty("TraceId", traceId))
        {
            await next();
        }
    });
    app.UseSerilogRequestLogging();
    app.UseHttpMetrics();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments($"/api/{DefaultApiVersion}", out var remaining))
    {
        context.Request.Path = remaining.HasValue ? remaining : "/";
    }

    context.Response.Headers["api-supported-versions"] = DefaultApiVersion;
    await next();
});

    app.MapGet("/health", (CatalogOptions opts) =>
        Results.Ok(new
        {
            service = "product-service",
            status = "ok",
            dataSource = string.IsNullOrWhiteSpace(opts.ConnectionString) ? "memory" : "postgresql"
        }));
    app.MapMetrics("/metrics");

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
            var match = await PostgresCatalog.GetByIdAsync(opts.ConnectionString, productId, includeInactive: false, ct);
            return match is null ? Results.NotFound() : Results.Ok(match);
        }

        return store.Products.TryGetValue(productId, out var product)
            ? Results.Ok(product)
            : Results.NotFound();
    });

    app.MapGet("/admin/products", async (string? q, ProductStore store, CatalogOptions opts, CancellationToken ct) =>
    {
        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            var list = await PostgresCatalog.ListAsync(opts.ConnectionString, q, includeInactive: true, ct);
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

    app.MapPost("/products", async (ProductDto request, ProductStore store, CatalogOptions opts, CancellationToken ct) =>
    {
        if (request.Price < 0)
        {
            return Results.BadRequest(new { error = "Price must be greater than or equal to zero." });
        }

        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            var created = await PostgresCatalog.CreateAsync(opts.ConnectionString, request, ct);
            return Results.Created($"/products/{created.ProductId}", created);
        }

        var product = request with
        {
            ProductId = request.ProductId == Guid.Empty ? Guid.NewGuid() : request.ProductId
        };
        store.Products[product.ProductId] = product;
        return Results.Created($"/products/{product.ProductId}", product);
    });

    app.MapPut("/products/{productId:guid}", async (Guid productId, ProductDto request, ProductStore store, CatalogOptions opts, CancellationToken ct) =>
    {
        if (request.Price < 0)
        {
            return Results.BadRequest(new { error = "Price must be greater than or equal to zero." });
        }

        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            var updated = await PostgresCatalog.UpdateAsync(
                opts.ConnectionString,
                productId,
                request with { ProductId = productId },
                ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }

        if (!store.Products.ContainsKey(productId))
        {
            return Results.NotFound();
        }

        var updatedProduct = request with { ProductId = productId };
        store.Products[productId] = updatedProduct;
        return Results.Ok(updatedProduct);
    });

    app.MapDelete("/products/{productId:guid}", async (Guid productId, ProductStore store, CatalogOptions opts, CancellationToken ct) =>
    {
        if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
        {
            var deactivated = await PostgresCatalog.DeactivateAsync(opts.ConnectionString, productId, ct);
            return deactivated ? Results.NoContent() : Results.NotFound();
        }

        if (!store.Products.TryGetValue(productId, out var existing))
        {
            return Results.NotFound();
        }

        store.Products[productId] = existing with { IsActive = false };
        return Results.NoContent();
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

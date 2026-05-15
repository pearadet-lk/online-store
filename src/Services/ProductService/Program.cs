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
            .AddOnlineStoreTraceExporters(builder.Configuration));
    builder.Services.AddOpenApi();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddSingleton(_ => new CatalogOptions(builder.Configuration.GetConnectionString("Catalog")));
    builder.Services.AddSingleton<IProductRepositoryResolver, ProductRepositoryResolver>();

    var app = builder.Build();

    var resolver = app.Services.GetRequiredService<IProductRepositoryResolver>();
    if (resolver.PostgresRepository is not null)
    {
        try
        {
            await resolver.PostgresRepository.EnsureSchemaAndSeedAsync(CancellationToken.None);
            app.Logger.LogInformation("Product catalog synchronized with PostgreSQL.");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "PostgreSQL unavailable; falling back to in-memory catalog.");
            app.Services.GetRequiredService<CatalogOptions>().ConnectionString = null;
            resolver.DisablePostgres();
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
    app.UseDefaultApiVersioning(DefaultApiVersion);
    app.UseRouting();

    app.MapGet("/health", (CatalogOptions opts) =>
        Results.Ok(new
        {
            service = "product-service",
            status = "ok",
            dataSource = string.IsNullOrWhiteSpace(opts.ConnectionString) ? "memory" : "postgresql"
        }));
    app.MapMetrics("/metrics");

    app.MapGet("/products", async (string? q, IProductRepositoryResolver repositoryResolver, CancellationToken ct) =>
    {
        var list = await repositoryResolver.Repository.ListAsync(q, includeInactive: false, ct);
        return Results.Ok(list);
    });

    app.MapGet("/products/{productId:guid}", async (Guid productId, IProductRepositoryResolver repositoryResolver, CancellationToken ct) =>
    {
        var match = await repositoryResolver.Repository.GetByIdAsync(productId, includeInactive: false, ct);
        return match is null ? Results.NotFound() : Results.Ok(match);
    });

    app.MapGet("/admin/products", async (string? q, IProductRepositoryResolver repositoryResolver, CancellationToken ct) =>
    {
        var list = await repositoryResolver.Repository.ListAsync(q, includeInactive: true, ct);
        return Results.Ok(list);
    });

    app.MapPost("/products", async (ProductDto request, IProductRepositoryResolver repositoryResolver, CancellationToken ct) =>
    {
        if (request.Price < 0)
        {
            return Results.BadRequest(new { error = "Price must be greater than or equal to zero." });
        }

        var created = await repositoryResolver.Repository.CreateAsync(request, ct);
        return Results.Created($"/products/{created.ProductId}", created);
    });

    app.MapPut("/products/{productId:guid}", async (Guid productId, ProductDto request, IProductRepositoryResolver repositoryResolver, CancellationToken ct) =>
    {
        if (request.Price < 0)
        {
            return Results.BadRequest(new { error = "Price must be greater than or equal to zero." });
        }

        var updated = await repositoryResolver.Repository.UpdateAsync(productId, request with { ProductId = productId }, ct);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    });

    app.MapDelete("/products/{productId:guid}", async (Guid productId, IProductRepositoryResolver repositoryResolver, CancellationToken ct) =>
    {
        var deactivated = await repositoryResolver.Repository.DeactivateAsync(productId, ct);
        if (!deactivated)
        {
            return Results.NotFound();
        }

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


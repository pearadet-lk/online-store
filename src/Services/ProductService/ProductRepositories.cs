using System.Collections.Concurrent;
using Contracts;
using Npgsql;

namespace ProductService;

internal interface IProductRepository
{
    Task<IReadOnlyList<ProductDto>> ListAsync(string? q, bool includeInactive, CancellationToken ct);
    Task<ProductDto?> GetByIdAsync(Guid productId, bool includeInactive, CancellationToken ct);
    Task<ProductDto> CreateAsync(ProductDto input, CancellationToken ct);
    Task<ProductDto?> UpdateAsync(Guid productId, ProductDto input, CancellationToken ct);
    Task<bool> DeactivateAsync(Guid productId, CancellationToken ct);
}

internal sealed class InMemoryProductRepository : IProductRepository
{
    private readonly ConcurrentDictionary<Guid, ProductDto> _products = new(
        CatalogSeed.DefaultProducts().ToDictionary(p => p.ProductId));

    public Task<IReadOnlyList<ProductDto>> ListAsync(string? q, bool includeInactive, CancellationToken ct)
    {
        var products = _products.Values.AsEnumerable();
        if (!includeInactive)
        {
            products = products.Where(x => x.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            products = products.Where(x =>
                x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                x.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyList<ProductDto>>(products.OrderBy(x => x.Name).ToList());
    }

    public Task<ProductDto?> GetByIdAsync(Guid productId, bool includeInactive, CancellationToken ct)
    {
        if (!_products.TryGetValue(productId, out var product))
        {
            return Task.FromResult<ProductDto?>(null);
        }

        if (!includeInactive && !product.IsActive)
        {
            return Task.FromResult<ProductDto?>(null);
        }

        return Task.FromResult<ProductDto?>(product);
    }

    public Task<ProductDto> CreateAsync(ProductDto input, CancellationToken ct)
    {
        var product = input with
        {
            ProductId = input.ProductId == Guid.Empty ? Guid.NewGuid() : input.ProductId
        };
        _products[product.ProductId] = product;
        return Task.FromResult(product);
    }

    public Task<ProductDto?> UpdateAsync(Guid productId, ProductDto input, CancellationToken ct)
    {
        if (!_products.ContainsKey(productId))
        {
            return Task.FromResult<ProductDto?>(null);
        }

        var updated = input with { ProductId = productId };
        _products[productId] = updated;
        return Task.FromResult<ProductDto?>(updated);
    }

    public Task<bool> DeactivateAsync(Guid productId, CancellationToken ct)
    {
        if (!_products.TryGetValue(productId, out var existing))
        {
            return Task.FromResult(false);
        }

        _products[productId] = existing with { IsActive = false };
        return Task.FromResult(true);
    }
}

internal sealed class PostgresProductRepository(string connectionString) : IProductRepository
{
    public async Task EnsureSchemaAndSeedAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        const string bootstrapSql = """
            CREATE SCHEMA IF NOT EXISTS product_service;
            CREATE TABLE IF NOT EXISTS product_service.products (
                product_id UUID PRIMARY KEY,
                sku VARCHAR(64) UNIQUE NOT NULL,
                name VARCHAR(255) NOT NULL,
                description TEXT,
                price NUMERIC(18, 2) NOT NULL CHECK (price >= 0),
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_product_active_name
                ON product_service.products (is_active, name);
            """;

        await using (var bootstrapCmd = new NpgsqlCommand(bootstrapSql, conn))
        {
            await bootstrapCmd.ExecuteNonQueryAsync(ct);
        }

        const string upsert = """
            INSERT INTO product_service.products (product_id, sku, name, description, price, is_active)
            VALUES (@id, @sku, @name, @desc, @price, TRUE)
            ON CONFLICT (product_id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                price = EXCLUDED.price,
                updated_at = NOW();
            """;

        foreach (var p in CatalogSeed.DefaultProducts())
        {
            await using var cmd = new NpgsqlCommand(upsert, conn);
            cmd.Parameters.AddWithValue("id", p.ProductId);
            cmd.Parameters.AddWithValue("sku", $"sku-{p.ProductId:N}");
            cmd.Parameters.AddWithValue("name", p.Name);
            cmd.Parameters.AddWithValue("desc", (object?)p.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("price", p.Price);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<ProductDto>> ListAsync(string? q, bool includeInactive, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var sql = """
            SELECT product_id, name, description, price, is_active
            FROM product_service.products
            WHERE (@includeInactive = TRUE OR is_active = TRUE)
            """;

        if (!string.IsNullOrWhiteSpace(q))
        {
            sql += " AND (name ILIKE @q OR description ILIKE @q)";
        }

        sql += " ORDER BY name";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("includeInactive", includeInactive);
        if (!string.IsNullOrWhiteSpace(q))
        {
            cmd.Parameters.AddWithValue("q", $"%{q}%");
        }

        var list = new List<ProductDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ProductDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetDecimal(3),
                reader.GetBoolean(4)));
        }

        return list;
    }

    public async Task<ProductDto?> GetByIdAsync(Guid productId, bool includeInactive, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT product_id, name, description, price, is_active
            FROM product_service.products
            WHERE product_id = @id
              AND (@includeInactive = TRUE OR is_active = TRUE)
            LIMIT 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", productId);
        cmd.Parameters.AddWithValue("includeInactive", includeInactive);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ProductDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetBoolean(4));
    }

    public async Task<ProductDto> CreateAsync(ProductDto input, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var productId = input.ProductId == Guid.Empty ? Guid.NewGuid() : input.ProductId;
        const string sql = """
            INSERT INTO product_service.products (product_id, sku, name, description, price, is_active)
            VALUES (@id, @sku, @name, @desc, @price, @active)
            RETURNING product_id, name, description, price, is_active;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", productId);
        cmd.Parameters.AddWithValue("sku", $"sku-{productId:N}");
        cmd.Parameters.AddWithValue("name", input.Name);
        cmd.Parameters.AddWithValue("desc", (object?)input.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("price", input.Price);
        cmd.Parameters.AddWithValue("active", input.IsActive);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ProductDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetBoolean(4));
    }

    public async Task<ProductDto?> UpdateAsync(Guid productId, ProductDto input, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE product_service.products
            SET name = @name,
                description = @desc,
                price = @price,
                is_active = @active,
                updated_at = NOW()
            WHERE product_id = @id
            RETURNING product_id, name, description, price, is_active;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", productId);
        cmd.Parameters.AddWithValue("name", input.Name);
        cmd.Parameters.AddWithValue("desc", (object?)input.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("price", input.Price);
        cmd.Parameters.AddWithValue("active", input.IsActive);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ProductDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.GetDecimal(3),
            reader.GetBoolean(4));
    }

    public async Task<bool> DeactivateAsync(Guid productId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE product_service.products
            SET is_active = FALSE,
                updated_at = NOW()
            WHERE product_id = @id;
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", productId);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }
}

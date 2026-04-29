using Contracts;
using Npgsql;

namespace ProductService;

internal static class PostgresCatalog
{
    public static async Task EnsureSchemaAndSeedAsync(string connectionString, CancellationToken ct)
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

        foreach (var p in DefaultProducts())
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

    public static async Task<IReadOnlyList<ProductDto>> ListAsync(string connectionString, string? q, CancellationToken ct)
        => await ListAsync(connectionString, q, includeInactive: false, ct);

    public static async Task<IReadOnlyList<ProductDto>> ListAsync(string connectionString, string? q, bool includeInactive, CancellationToken ct)
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

    public static async Task<ProductDto?> GetByIdAsync(string connectionString, Guid productId, bool includeInactive, CancellationToken ct)
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

    public static async Task<ProductDto> CreateAsync(string connectionString, ProductDto input, CancellationToken ct)
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

    public static async Task<ProductDto?> UpdateAsync(string connectionString, Guid productId, ProductDto input, CancellationToken ct)
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

    public static async Task<bool> DeactivateAsync(string connectionString, Guid productId, CancellationToken ct)
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

    private static IEnumerable<ProductDto> DefaultProducts()
    {
        yield return new ProductDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Starter Keyboard",
            "Entry-level keyboard",
            39.99m,
            true);
        yield return new ProductDto(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "Gaming Mouse",
            "RGB gaming mouse",
            59.99m,
            true);
    }
}

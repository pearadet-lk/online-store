using Contracts;
using Npgsql;

namespace ProductService;

internal static class PostgresCatalog
{
    public static async Task EnsureSchemaAndSeedAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

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
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var sql = """
            SELECT product_id, name, description, price, is_active
            FROM product_service.products
            WHERE is_active = TRUE
            """;

        if (!string.IsNullOrWhiteSpace(q))
        {
            sql += " AND (name ILIKE @q OR description ILIKE @q)";
        }

        sql += " ORDER BY name";

        await using var cmd = new NpgsqlCommand(sql, conn);
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

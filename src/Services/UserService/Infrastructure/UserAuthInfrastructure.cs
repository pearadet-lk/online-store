using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Contracts;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

internal sealed record UserAuthOptions(
    string? ConnectionString,
    string JwtIssuer,
    string JwtAudience,
    string JwtSigningKey,
    int AccessTokenMinutes,
    int RefreshTokenDays)
{
    public string? ConnectionString { get; set; } = ConnectionString;
}

internal sealed class PasswordHasher
{
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split('.', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

internal sealed class AuthTokenService(UserAuthOptions options)
{
    private readonly SymmetricSecurityKey _securityKey = new(Encoding.UTF8.GetBytes(options.JwtSigningKey));

    public TokenPair CreateTokenPair(UserProfileDto profile)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(options.AccessTokenMinutes);
        var credentials = new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: options.JwtIssuer,
            audience: options.JwtAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, profile.UserId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, profile.Email),
                new Claim(ClaimTypes.NameIdentifier, profile.UserId.ToString()),
                new Claim(ClaimTypes.Name, profile.FullName ?? profile.Email)
            ],
            notBefore: now,
            expires: expires,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return new TokenPair(accessToken, refreshToken, DateTime.SpecifyKind(expires, DateTimeKind.Utc));
    }
}

internal static class UserAuthDb
{
    public static async Task EnsureSchemaAsync(string connectionString, Guid demoUserId, string demoEmail, string demoFullName, string demoPasswordHash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        const string bootstrapSql = """
            CREATE SCHEMA IF NOT EXISTS user_service;
            CREATE TABLE IF NOT EXISTS user_service.users (
                user_id UUID PRIMARY KEY,
                email VARCHAR(255) UNIQUE NOT NULL,
                password_hash TEXT NOT NULL,
                full_name VARCHAR(200) NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_user_email ON user_service.users (email);
            CREATE TABLE IF NOT EXISTS user_service.refresh_tokens (
                token_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id UUID NOT NULL,
                token_hash VARCHAR(128) NOT NULL UNIQUE,
                expires_at TIMESTAMPTZ NOT NULL,
                revoked_at TIMESTAMPTZ NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                replaced_by_token_hash VARCHAR(128) NULL
            );
            CREATE INDEX IF NOT EXISTS idx_user_refresh_tokens_user
                ON user_service.refresh_tokens (user_id, expires_at DESC);
            CREATE INDEX IF NOT EXISTS idx_user_refresh_tokens_active
                ON user_service.refresh_tokens (token_hash, expires_at)
                WHERE revoked_at IS NULL;
            """;
        await using (var bootstrap = new NpgsqlCommand(bootstrapSql, conn))
        {
            await bootstrap.ExecuteNonQueryAsync(ct);
        }

        const string upsertDemo = """
            INSERT INTO user_service.users (user_id, email, password_hash, full_name)
            VALUES (@id, @email, @passwordHash, @fullName)
            ON CONFLICT (user_id) DO UPDATE SET
                email = EXCLUDED.email,
                password_hash = EXCLUDED.password_hash,
                full_name = EXCLUDED.full_name,
                updated_at = NOW();
            """;
        await using var upsert = new NpgsqlCommand(upsertDemo, conn);
        upsert.Parameters.AddWithValue("id", demoUserId);
        upsert.Parameters.AddWithValue("email", demoEmail.ToLowerInvariant());
        upsert.Parameters.AddWithValue("passwordHash", demoPasswordHash);
        upsert.Parameters.AddWithValue("fullName", demoFullName);
        await upsert.ExecuteNonQueryAsync(ct);
    }

    public static async Task<UserAccount?> GetUserByEmailAsync(string connectionString, string email, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            SELECT user_id, email, full_name, created_at, password_hash
            FROM user_service.users
            WHERE email = @email
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("email", email);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var profile = new UserProfileDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
        return new UserAccount(profile, reader.GetString(4));
    }

    public static async Task<UserAccount?> GetUserByIdAsync(string connectionString, Guid userId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            SELECT user_id, email, full_name, created_at, password_hash
            FROM user_service.users
            WHERE user_id = @id
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var profile = new UserProfileDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
        return new UserAccount(profile, reader.GetString(4));
    }

    public static async Task<UserAccount> CreateUserAsync(string connectionString, string email, string fullName, string passwordHash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            INSERT INTO user_service.users (user_id, email, password_hash, full_name)
            VALUES (@id, @email, @passwordHash, @fullName)
            RETURNING user_id, email, full_name, created_at, password_hash;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("passwordHash", passwordHash);
        cmd.Parameters.AddWithValue("fullName", fullName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var profile = new UserProfileDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
        return new UserAccount(profile, reader.GetString(4));
    }

    public static async Task SaveRefreshTokenAsync(string connectionString, Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            INSERT INTO user_service.refresh_tokens (user_id, token_hash, expires_at)
            VALUES (@userId, @tokenHash, @expiresAt);
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);
        cmd.Parameters.AddWithValue("expiresAt", expiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<RefreshTokenRecord?> GetRefreshTokenAsync(string connectionString, string tokenHash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            SELECT user_id, token_hash, expires_at, revoked_at, replaced_by_token_hash
            FROM user_service.refresh_tokens
            WHERE token_hash = @tokenHash
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new RefreshTokenRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public static async Task RotateRefreshTokenAsync(string connectionString, string oldTokenHash, string newTokenHash, DateTimeOffset newExpiresAt, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string revokeSql = """
            UPDATE user_service.refresh_tokens
            SET revoked_at = NOW(),
                replaced_by_token_hash = @newTokenHash
            WHERE token_hash = @oldTokenHash;
            """;
        await using (var revoke = new NpgsqlCommand(revokeSql, conn, tx))
        {
            revoke.Parameters.AddWithValue("oldTokenHash", oldTokenHash);
            revoke.Parameters.AddWithValue("newTokenHash", newTokenHash);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = """
            INSERT INTO user_service.refresh_tokens (user_id, token_hash, expires_at)
            SELECT user_id, @newTokenHash, @expiresAt
            FROM user_service.refresh_tokens
            WHERE token_hash = @oldTokenHash
            LIMIT 1;
            """;
        await using (var insert = new NpgsqlCommand(insertSql, conn, tx))
        {
            insert.Parameters.AddWithValue("oldTokenHash", oldTokenHash);
            insert.Parameters.AddWithValue("newTokenHash", newTokenHash);
            insert.Parameters.AddWithValue("expiresAt", newExpiresAt);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public static async Task RevokeRefreshTokenAsync(string connectionString, string tokenHash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        const string sql = """
            UPDATE user_service.refresh_tokens
            SET revoked_at = NOW()
            WHERE token_hash = @tokenHash
              AND revoked_at IS NULL;
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tokenHash", tokenHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class UserStore
{
    public ConcurrentDictionary<Guid, UserAccount> UsersById { get; } = new();
    public ConcurrentDictionary<string, UserAccount> UsersByEmail { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, RefreshTokenRecord> RefreshTokens { get; } = new(StringComparer.Ordinal);

    public UserStore(PasswordHasher hasher)
    {
        var demoUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        const string demoEmail = "demo@example.com";
        const string demoPassword = "demo-password";
        const string demoFullName = "Demo User";
        var demo = new UserAccount(
            new UserProfileDto(demoUserId, demoEmail, demoFullName, DateTimeOffset.UtcNow),
            hasher.Hash(demoPassword));
        UsersById[demo.Profile.UserId] = demo;
        UsersByEmail[demoEmail] = demo;
    }
}

internal sealed record UserAccount(UserProfileDto Profile, string PasswordHash);
internal sealed record TokenPair(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt);
internal sealed record RefreshTokenRequest(string RefreshToken);
internal sealed record RefreshTokenRecord(Guid UserId, string TokenHash, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, string? ReplacedByTokenHash);

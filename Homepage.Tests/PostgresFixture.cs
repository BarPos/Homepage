using Homepage.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Homepage.Tests;

/// <summary>
/// One PostgreSQL 16 container (Docker, dev/test only) for the Postgres collection; each test gets its own database.
/// Set HOMEPAGE_TEST_PG to an admin connection string to use an existing server instead of Docker.
/// Pattern from DomainNotice (MIT).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _admin = "";

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("HOMEPAGE_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external)) { _admin = external; return; }
        _container = new PostgreSqlBuilder("postgres:16").WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off").Build();
        await _container.StartAsync();
        _admin = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(_admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_admin) { Database = name, MaxPoolSize = 20 }.ConnectionString;
    }

    public static AppDbContext Context(string cs)
    {
        var o = new DbContextOptionsBuilder<AppDbContext>();
        ServiceCollectionExtensions.ConfigureDb(o, cs);
        return new AppDbContext(o.Options);
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>A minimal IDbContextFactory over one connection string.</summary>
public sealed class TestDbFactory(string cs) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => PostgresFixture.Context(cs);
}

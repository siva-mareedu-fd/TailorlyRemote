using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL 16 container per test run, migrated with the same code path as <c>Tailorly.App --migrate</c>
/// on an empty database.
/// </summary>
public sealed class PostgresAppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("tailorly")
        .WithUsername("tailorly")
        .WithPassword("tailorly-test")
        .Build();

    public TailorlyAppFactory Factory { get; private set; } = null!;
    public string ConnectionString => _db.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _db.StartAsync();
        Factory = new TailorlyAppFactory(ConnectionString);
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MigrationRunner>().MigrateAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _db.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresAppFixture>
{
    public const string Name = "postgres";
}

using Microsoft.EntityFrameworkCore;
using Whatsapp.Outbox;

namespace Whatsapp.Tests.Outbox;

/// <summary>
/// The schema is created by committed migrations. Changing an entity without adding a migration leaves the model
/// snapshot behind the model, and this test fails until <c>dotnet ef migrations add</c> has been run.
/// </summary>
public sealed class MigrationTests
{
    private static OutboxDbContext PostgresContext() =>
        new(new DbContextOptionsBuilder<OutboxDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened;Username=none;Password=none")
            .Options);

    [Fact]
    public void The_committed_model_snapshot_matches_the_current_model()
    {
        using var db = PostgresContext();

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "The model changed without a migration. Run: dotnet ef migrations add <Name> --project src/Whatsapp.Outbox");
    }

    [Fact]
    public void The_migrations_are_committed_in_order_starting_with_the_initial_one()
    {
        using var db = PostgresContext();

        var migrations = db.Database.GetMigrations().ToList();

        migrations.ShouldNotBeEmpty();
        migrations[0].ShouldEndWith("_InitialCreate");
        migrations.ShouldBe([.. migrations.Order(StringComparer.Ordinal)]);
    }
}

public sealed class SchemaModeTests
{
    private const string Postgres = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    [Theory]
    [InlineData(OutboxSchemaMode.Auto, Postgres, OutboxSchemaMode.Migrate)]
    [InlineData(OutboxSchemaMode.Auto, Sqlite, OutboxSchemaMode.EnsureCreated)]
    [InlineData(OutboxSchemaMode.Migrate, Postgres, OutboxSchemaMode.Migrate)]
    [InlineData(OutboxSchemaMode.EnsureCreated, Postgres, OutboxSchemaMode.EnsureCreated)]
    public void Auto_migrates_on_postgres_and_creates_the_schema_elsewhere(OutboxSchemaMode configured, string provider, OutboxSchemaMode expected) =>
        OutboxDatabaseInitializer.Resolve(configured, provider).ShouldBe(expected);
}

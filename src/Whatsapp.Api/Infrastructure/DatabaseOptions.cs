using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Whatsapp.Api.Infrastructure;

public enum DatabaseProvider
{
    Postgres = 0,
    Sqlite = 1,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Postgres;

    public string ConnectionString { get; set; } = string.Empty;

    internal void Configure(DbContextOptionsBuilder builder)
    {
        switch (Provider)
        {
            case DatabaseProvider.Postgres:
                builder.UseNpgsql(ConnectionString);
                break;
            case DatabaseProvider.Sqlite:
                builder.UseSqlite(ConnectionString);
                break;
            default:
                throw new InvalidOperationException($"Unsupported database provider '{Provider}'.");
        }
    }
}

internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail("Database:ConnectionString is required.")
            : !Enum.IsDefined(options.Provider)
                ? ValidateOptionsResult.Fail("Database:Provider must be Postgres or Sqlite.")
                : ValidateOptionsResult.Success;
}

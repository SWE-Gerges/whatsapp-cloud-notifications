using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Whatsapp.Outbox;

/// <summary>
/// Inserts a row and reports through the affected-row count whether the unique key was already taken, so the normal
/// duplicate path raises no exception and therefore leaves no error-level log line behind. A failure that is not a
/// key conflict still throws and is logged by EF Core as usual.
/// </summary>
internal static class ConflictFreeInsert
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <param name="conflictTarget">The columns of the unique index that decides the conflict.</param>
    /// <returns>1 when the row was inserted, 0 when the key already existed.</returns>
    public static Task<int> ExecuteAsync(
        DatabaseFacade database,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?> values,
        IReadOnlyList<string> conflictTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (columns.Count != values.Count)
        {
            throw new ArgumentException("Every column needs exactly one value.", nameof(values));
        }

        var sql = BuildSql(database.ProviderName, table, columns, conflictTarget);
        return database.ExecuteSqlRawAsync(sql, [.. values!], cancellationToken);
    }

    internal static string BuildSql(
        string? provider,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictTarget)
    {
        var verb = provider switch
        {
            PostgresProvider => "INSERT",
            SqliteProvider => "INSERT OR IGNORE",
            _ => throw new NotSupportedException($"The database provider '{provider}' is not supported."),
        };

        var sql = new StringBuilder(verb)
            .Append(" INTO ").Append(Quote(table))
            .Append(" (").AppendJoin(", ", columns.Select(Quote)).Append(") VALUES (")
            .AppendJoin(", ", columns.Select((_, i) => "{" + i + "}")).Append(')');

        if (provider == PostgresProvider)
        {
            sql.Append(" ON CONFLICT (").AppendJoin(", ", conflictTarget.Select(Quote)).Append(") DO NOTHING");
        }

        return sql.ToString();
    }

    private static string Quote(string identifier) => "\"" + identifier + "\"";
}

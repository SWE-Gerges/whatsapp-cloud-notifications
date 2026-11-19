using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whatsapp.Outbox;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Outbox;

/// <summary>
/// The duplicate path of the claim-first insert is normal, so it must not be an error anywhere: not an exception,
/// not an error-level log line. Real failures must stay just as visible.
/// </summary>
public sealed class ConflictFreeEnqueueTests : IDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly OutboxHarness _harness;

    public ConflictFreeEnqueueTests() => _harness = new OutboxHarness(logs: _logs);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task A_duplicate_enqueue_logs_no_error_level_line_from_any_component()
    {
        var first = await _harness.EnqueueTemplateAsync();
        var second = await _harness.EnqueueTemplateAsync();
        var third = await _harness.EnqueueTemplateAsync();

        first.Duplicate.ShouldBeFalse();
        second.Duplicate.ShouldBeTrue();
        third.Duplicate.ShouldBeTrue();
        _harness.Database.Rows().ShouldHaveSingleItem();

        // The capture sees the executed commands, so an empty error list is not a vacuous result.
        _logs.Entries.ShouldContain(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command");
        _logs.AtLeast(LogLevel.Warning).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_statement_is_still_logged_as_an_error()
    {
        await using var db = await _harness.Database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("INSERT INTO \"NoSuchTable\" (\"Id\") VALUES (1)", TestContext.Current.CancellationToken));

        _logs.AtLeast(LogLevel.Error).ShouldContain(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command");
    }

    [Fact]
    public void The_claim_insert_writes_every_required_column()
    {
        using var db = _harness.Database.Factory.CreateDbContext();
        var entity = db.Model.FindEntityType(typeof(NotificationLog))!;
        var required = entity.GetProperties().Where(p => !p.IsNullable).Select(p => p.Name);

        NotificationEnqueuer.InsertColumns.ShouldBeSubsetOf(entity.GetProperties().Select(p => p.Name));
        required.ShouldBeSubsetOf(NotificationEnqueuer.InsertColumns);
        NotificationEnqueuer.KeyColumns.ShouldBeSubsetOf(NotificationEnqueuer.InsertColumns);
    }

    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "INSERT INTO \"T\" (\"A\", \"B\") VALUES ({0}, {1}) ON CONFLICT (\"A\") DO NOTHING")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "INSERT OR IGNORE INTO \"T\" (\"A\", \"B\") VALUES ({0}, {1})")]
    public void The_insert_sql_is_conflict_free_for_each_provider(string provider, string expected) =>
        ConflictFreeInsert.BuildSql(provider, "T", ["A", "B"], ["A"]).ShouldBe(expected);

    [Fact]
    public void An_unknown_provider_is_rejected() =>
        Should.Throw<NotSupportedException>(() => ConflictFreeInsert.BuildSql("Other", "T", ["A"], ["A"]));
}

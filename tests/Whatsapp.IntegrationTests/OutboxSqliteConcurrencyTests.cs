using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.IntegrationTests;

/// <summary>
/// The outbox guarantees under real concurrency on a SQLite database file: one row per key, one send per row, and no
/// error-level log line on the normal duplicate path. The same insert on PostgreSQL (ON CONFLICT DO NOTHING) is
/// covered only by a test of the generated statement; it has not been run against a PostgreSQL server here.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OutboxSqliteConcurrencyTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"outbox-concurrency-{Guid.NewGuid():N}.db");
    private readonly CountingClient _client = new();
    private readonly CapturingLoggerProvider _logs = new();
    private ServiceProvider _provider = null!;
    private IDbContextFactory<OutboxDbContext> _dbFactory = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
        services.AddDbContextFactory<OutboxDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<OutboxDbContext>>();

        await using var db = await _dbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless.
        }
    }

    private NotificationEnqueuer CreateEnqueuer() =>
        new(_dbFactory, TimeProvider.System, NullLogger<NotificationEnqueuer>.Instance);

    private OutboxProcessor CreateProcessor(int batchSize = 25, int concurrency = 4) => new(
        _dbFactory,
        _client,
        new FixedTenantResolver(),
        new ConversationWindowPolicy(new InMemoryConversationWindowStore(TimeProvider.System, Options.Create(new ConversationWindowOptions()))),
        new TemplateRegistry(DefaultTemplates.All),
        new ReceiptEffects(_dbFactory, CreateEnqueuer(), TimeProvider.System, NullLogger<ReceiptEffects>.Instance),
        new ExponentialBackoff(new OutboxOptions(), Random.Shared.NextDouble),
        Options.Create(new OutboxOptions { BatchSize = batchSize, Concurrency = concurrency, MaxAttempts = 3 }),
        Options.Create(new ReconciliationOptions()),
        TimeProvider.System,
        NullLogger<OutboxProcessor>.Instance);

    private static EnqueueNotification Request(string reference, string recipient = "+12025550100") => new(
        "tenant-a",
        recipient,
        "booking-status-changed",
        reference,
        NotificationKind.Template,
        TemplateNames.BookingStatusUpdated,
        "en",
        ["Sam", reference, "Confirmed"]);

    private async Task<List<NotificationLog>> RowsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.NotificationLogs.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Many_parallel_enqueues_of_the_same_key_create_exactly_one_row_and_one_send()
    {
        var enqueuer = CreateEnqueuer();

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => enqueuer.EnqueueAsync(Request("bk-1"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        results.Count(r => !r.Duplicate).ShouldBe(1);
        results.Count(r => r.Duplicate).ShouldBe(63);
        results.Select(r => r.Id).Distinct().ShouldHaveSingleItem();
        (await RowsAsync()).ShouldHaveSingleItem();

        var processor = CreateProcessor();
        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);
        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        _client.TotalSends.ShouldBe(1);
        (await RowsAsync()).ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task Parallel_duplicate_enqueues_write_no_error_level_log_line()
    {
        var enqueuer = CreateEnqueuer();

        await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => enqueuer.EnqueueAsync(Request("bk-1"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        (await RowsAsync()).ShouldHaveSingleItem();
        _logs.Entries.ShouldContain(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command");
        _logs.AtLeast(LogLevel.Warning).ShouldBeEmpty();
    }

    [Fact]
    public async Task Parallel_enqueues_across_many_keys_lose_nothing_and_duplicate_nothing()
    {
        var enqueuer = CreateEnqueuer();
        var requests = Enumerable.Range(0, 40).SelectMany(i => Enumerable.Repeat(Request($"bk-{i}"), 4)).ToList();

        var results = await Task.WhenAll(requests
            .Select(r => Task.Run(() => enqueuer.EnqueueAsync(r, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        results.Count(r => !r.Duplicate).ShouldBe(40);
        (await RowsAsync()).Count.ShouldBe(40);
    }

    [Fact]
    public async Task The_database_itself_rejects_a_second_row_for_the_same_key()
    {
        var enqueuer = CreateEnqueuer();
        await enqueuer.EnqueueAsync(Request("bk-1"), TestContext.Current.CancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-a",
            Recipient = "+12025550100",
            EventKey = "booking-status-changed",
            ReferenceKey = "bk-1",
            Kind = NotificationKind.FreeText,
            Text = "x",
            Status = NotificationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
        });

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parallel_dispatchers_send_every_row_exactly_once()
    {
        const int rows = 120;
        var enqueuer = CreateEnqueuer();
        await Task.WhenAll(Enumerable.Range(0, rows)
            .Select(i => enqueuer.EnqueueAsync(Request($"bk-{i}"), TestContext.Current.CancellationToken)));

        var dispatchers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var processor = CreateProcessor();
            while (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken) > 0)
            {
                // Keep claiming until nothing is due.
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(dispatchers);

        var stored = await RowsAsync();
        stored.Count.ShouldBe(rows);
        stored.ShouldAllBe(r => r.Status == NotificationStatus.Sent && r.Attempts == 1);
        _client.TotalSends.ShouldBe(rows);
        _client.SendsPerReference.Count.ShouldBe(rows);
        _client.SendsPerReference.Values.ShouldAllBe(count => count == 1);
    }

    [Fact]
    public async Task Instants_and_parameters_round_trip_through_the_database()
    {
        var enqueuer = CreateEnqueuer();
        var result = await enqueuer.EnqueueAsync(
            Request("bk-é") with { TemplateParameters = ["Zoë", "REF \"1\"", "مؤكد"] },
            TestContext.Current.CancellationToken);

        var row = (await RowsAsync()).Single(r => r.Id == result.Id);

        row.TemplateParameters.ShouldBe(["Zoë", "REF \"1\"", "مؤكد"]);
        row.ReferenceKey.ShouldBe("bk-é");
        row.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        (DateTimeOffset.UtcNow - row.CreatedAt).ShouldBeLessThan(TimeSpan.FromMinutes(1));
    }

    private sealed class FixedTenantResolver : ITenantOptionsResolver
    {
        public ValueTask<WhatsappTenantOptions> ResolveAsync(string tenantId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WhatsappTenantOptions
            {
                TenantId = tenantId,
                Enabled = true,
                PhoneNumberId = "100200300",
                AccessToken = "token-for-tests-only",
                BaseUrl = "https://graph.example.com/v1",
            });
    }

    private sealed class CountingClient : IWhatsappClient
    {
        private readonly ConcurrentDictionary<string, int> _perReference = new();
        private int _total;

        public int TotalSends => Volatile.Read(ref _total);

        public IReadOnlyDictionary<string, int> SendsPerReference => _perReference;

        public async Task<WhatsappSendResult> SendAsync(WhatsappMessage message, WhatsappTenantOptions options, CancellationToken cancellationToken)
        {
            // A short pause widens the window in which a double claim would show up.
            await Task.Delay(2, cancellationToken);
            Interlocked.Increment(ref _total);
            if (message is WhatsappTemplateMessage template)
            {
                _perReference.AddOrUpdate(template.Parameters[1], 1, (_, count) => count + 1);
            }

            return new WhatsappSendResult($"wamid.{Guid.NewGuid():N}", null);
        }
    }
}

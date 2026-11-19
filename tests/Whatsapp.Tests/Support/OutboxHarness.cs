using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Templates;

namespace Whatsapp.Tests.Support;

/// <summary>A throw-away SQLite database holding the outbox schema.</summary>
internal sealed class SqliteOutboxDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"outbox-tests-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;

    public SqliteOutboxDatabase(ILoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });
        services.AddDbContextFactory<OutboxDbContext>(o => o.UseSqlite(ConnectionString));
        _provider = services.BuildServiceProvider();
        Factory = _provider.GetRequiredService<IDbContextFactory<OutboxDbContext>>();

        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public string ConnectionString => $"Data Source={_path}";

    public IDbContextFactory<OutboxDbContext> Factory { get; }

    public List<NotificationLog> Rows()
    {
        using var db = Factory.CreateDbContext();
        return [.. db.NotificationLogs.AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)];
    }

    public NotificationLog Row(Guid id)
    {
        using var db = Factory.CreateDbContext();
        return db.NotificationLogs.AsNoTracking().Single(x => x.Id == id);
    }

    public void Insert(NotificationLog row)
    {
        using var db = Factory.CreateDbContext();
        db.NotificationLogs.Add(row);
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless.
        }
    }
}

/// <summary>Wires the outbox services against SQLite, a fake clock and a substituted Cloud API client.</summary>
internal sealed class OutboxHarness : IDisposable
{
    public OutboxHarness(Action<OutboxOptions>? configure = null, ILoggerProvider? logs = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        Database = new SqliteOutboxDatabase(logs);

        Options = new OutboxOptions
        {
            MaxAttempts = 3,
            BatchSize = 50,
            Concurrency = 1,
            LeaseDuration = TimeSpan.FromMinutes(2),
            RetryBaseDelay = TimeSpan.FromSeconds(30),
            RetryMaxDelay = TimeSpan.FromHours(1),
        };
        configure?.Invoke(Options);

        Client = Substitute.For<IWhatsappClient>();
        Client.SendAsync(default!, default!, default)
            .ReturnsForAnyArgs(_ => Task.FromResult(new WhatsappSendResult("wamid.TEST", "12025550100")));

        Tenants = Substitute.For<ITenantOptionsResolver>();
        Tenants.ResolveAsync(default!, default)
            .ReturnsForAnyArgs(call => new ValueTask<WhatsappTenantOptions>(TestData.Tenant(call.ArgAt<string>(0))));

        WindowStore = new InMemoryConversationWindowStore(Time, Microsoft.Extensions.Options.Options.Create(new ConversationWindowOptions()));
        Enqueuer = new NotificationEnqueuer(Database.Factory, Time, NullLogger<NotificationEnqueuer>.Instance);
        Store = new NotificationStore(Database.Factory, Time);
        Effects = new ReceiptEffects(Database.Factory, Enqueuer, Time, NullLogger<ReceiptEffects>.Instance);
        Receipts = new DeliveryReceiptRecorder(Database.Factory, Effects, Time);
    }

    public FakeTimeProvider Time { get; }

    public SqliteOutboxDatabase Database { get; }

    public OutboxOptions Options { get; }

    /// <summary>Settings of the reconciler and of the grace period the dispatcher gives an unconfirmed row. Change them before creating a processor.</summary>
    public ReconciliationOptions Reconciliation { get; } = new();

    public IWhatsappClient Client { get; }

    public ITenantOptionsResolver Tenants { get; }

    public InMemoryConversationWindowStore WindowStore { get; }

    public INotificationEnqueuer Enqueuer { get; }

    public INotificationStore Store { get; }

    public IReceiptEffects Effects { get; }

    public IDeliveryReceiptRecorder Receipts { get; }

    /// <summary>Random source of the backoff. 1.0 yields the full computed delay.</summary>
    public double Random { get; set; } = 1.0;

    public OutboxProcessor CreateProcessor() => new(
        Database.Factory,
        Client,
        Tenants,
        new ConversationWindowPolicy(WindowStore),
        new TemplateRegistry(DefaultTemplates.All),
        Effects,
        new ExponentialBackoff(Options, () => Random),
        Microsoft.Extensions.Options.Options.Create(Options),
        Microsoft.Extensions.Options.Options.Create(Reconciliation),
        Time,
        NullLogger<OutboxProcessor>.Instance);

    public ReconciliationProcessor CreateReconciler() => new(
        Database.Factory,
        Effects,
        Microsoft.Extensions.Options.Options.Create(Reconciliation),
        Microsoft.Extensions.Options.Options.Create(Options),
        Time,
        NullLogger<ReconciliationProcessor>.Instance);

    public Task<EnqueueResult> EnqueueTemplateAsync(
        string reference = "bk-1",
        string recipient = TestData.Recipient,
        string tenantId = "tenant-a",
        string templateName = TemplateNames.BookingStatusUpdated,
        string? language = "en",
        IReadOnlyList<string>? parameters = null) =>
        Enqueuer.EnqueueAsync(
            new EnqueueNotification(
                tenantId,
                recipient,
                "booking-status-changed",
                reference,
                NotificationKind.Template,
                templateName,
                language,
                parameters ?? ["Sam", "REF-1", "Confirmed"]),
            TestContext.Current.CancellationToken);

    public Task<EnqueueResult> EnqueueFreeTextAsync(
        string reference = "ft-1",
        string recipient = TestData.Recipient,
        string tenantId = "tenant-a",
        string text = "Your table is ready.",
        string? fallbackTemplate = null) =>
        Enqueuer.EnqueueAsync(
            new EnqueueNotification(
                tenantId,
                recipient,
                "custom",
                reference,
                NotificationKind.FreeText,
                fallbackTemplate,
                fallbackTemplate is null ? null : "en",
                fallbackTemplate is null ? null : ["Sam", "REF-1", "Confirmed"],
                text),
            TestContext.Current.CancellationToken);

    public void FailWith(WhatsappApiException exception) =>
        Client.SendAsync(default!, default!, default)
            .ReturnsForAnyArgs(_ => Task.FromException<WhatsappSendResult>(exception));

    public void TenantIs(WhatsappTenantOptions tenant) =>
        Tenants.ResolveAsync(default!, default)
            .ReturnsForAnyArgs(new ValueTask<WhatsappTenantOptions>(tenant));

    public void TenantFails(Exception exception) =>
        Tenants.ResolveAsync(default!, default)
            .ReturnsForAnyArgs(_ => ValueTask.FromException<WhatsappTenantOptions>(exception));

    public void ClientDoes(Func<NSubstitute.Core.CallInfo, Task<WhatsappSendResult>> behaviour) =>
        Client.SendAsync(default!, default!, default).ReturnsForAnyArgs(behaviour);

    public void Succeed(string messageId = "wamid.TEST") =>
        Client.SendAsync(default!, default!, default)
            .ReturnsForAnyArgs(_ => Task.FromResult(new WhatsappSendResult(messageId, null)));

    public IReadOnlyList<WhatsappMessage> SentMessages() =>
        [.. Client.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IWhatsappClient.SendAsync))
            .Select(c => (WhatsappMessage)c.GetArguments()[0]!)];

    public void Dispose() => Database.Dispose();
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox: database context factory, enqueuer, store, window policy and the background dispatcher.
    /// The caller picks the database provider through <paramref name="configureDatabase"/>.
    /// Requires <see cref="IWhatsappClient"/>, <see cref="ITenantOptionsResolver"/> and the template registry to be registered.
    /// </summary>
    public static IServiceCollection AddWhatsappOutbox(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        ArgumentNullException.ThrowIfNull(configureDatabase);
        return services.AddWhatsappOutbox(configuration, (_, builder) => configureDatabase(builder));
    }

    /// <summary>Same as the other overload, but the database is configured lazily from the built service provider.</summary>
    public static IServiceCollection AddWhatsappOutbox(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IServiceProvider, DbContextOptionsBuilder> configureDatabase)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configureDatabase);

        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<OutboxDbContext>(configureDatabase);

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OutboxOptions>, OutboxOptionsValidator>());

        services.AddOptions<ReconciliationOptions>()
            .Bind(configuration.GetSection(ReconciliationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ReconciliationOptions>, ReconciliationOptionsValidator>());

        services.AddOptions<ConversationWindowOptions>()
            .Bind(configuration.GetSection(ConversationWindowOptions.SectionName))
            .Validate(
                o => o.Duration > TimeSpan.Zero && o.Duration <= TimeSpan.FromHours(24),
                "ConversationWindow:Duration must be between zero and 24 hours (note that 24:00:00 parses as 24 days; use 1.00:00:00).")
            .ValidateOnStart();

        services.TryAddSingleton<INotificationEnqueuer, NotificationEnqueuer>();
        services.TryAddSingleton<INotificationStore, NotificationStore>();
        services.TryAddSingleton<IConversationWindowStore, EfConversationWindowStore>();
        services.TryAddSingleton<IConversationWindowPolicy, ConversationWindowPolicy>();
        services.TryAddSingleton<IInboundMessageRecorder, InboundMessageRecorder>();
        services.TryAddSingleton<IReceiptEffects, ReceiptEffects>();
        services.TryAddSingleton<IDeliveryReceiptRecorder, DeliveryReceiptRecorder>();
        services.TryAddSingleton<IOutboxBackoff>(sp =>
            new ExponentialBackoff(sp.GetRequiredService<IOptions<OutboxOptions>>().Value, Random.Shared.NextDouble));
        services.TryAddSingleton<OutboxDispatcherStatus>();
        services.TryAddScoped<OutboxProcessor>();
        services.TryAddSingleton<ReconciliationProcessor>();

        // Order matters: the schema must exist before the dispatcher and the reconciler start polling.
        services.AddHostedService<OutboxDatabaseInitializer>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<ReconciliationService>();
        return services;
    }
}

internal sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        var failures = new List<string>();
        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("Outbox:PollInterval must be positive.");
        }

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add("Outbox:LeaseDuration must be positive.");
        }

        if (options.RetryBaseDelay <= TimeSpan.Zero)
        {
            failures.Add("Outbox:RetryBaseDelay must be positive.");
        }

        if (options.RetryMaxDelay < options.RetryBaseDelay)
        {
            failures.Add("Outbox:RetryMaxDelay must not be shorter than RetryBaseDelay.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Prepares the schema on startup as <see cref="OutboxOptions.SchemaMode"/> says.</summary>
internal sealed class OutboxDatabaseInitializer(
    IDbContextFactory<OutboxDbContext> dbFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDatabaseInitializer> logger) : IHostedService
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.SchemaMode == OutboxSchemaMode.None)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var mode = Resolve(options.Value.SchemaMode, db.Database.ProviderName);
        if (mode == OutboxSchemaMode.EnsureCreated)
        {
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (db.Database.ProviderName != PostgresProvider)
        {
            throw new InvalidOperationException(
                $"The committed migrations target PostgreSQL, not '{db.Database.ProviderName}'. "
                + "Set Whatsapp:Outbox:SchemaMode to EnsureCreated for SQLite.");
        }

        // The existence probe comes first so that a missing database is created quietly instead of
        // surfacing as a failed connection when the migrator opens it.
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("The database does not exist yet and will be created");
            await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
        }

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Turns <see cref="OutboxSchemaMode.Auto"/> into the mode that suits the provider.</summary>
    internal static OutboxSchemaMode Resolve(OutboxSchemaMode configured, string? providerName) =>
        configured != OutboxSchemaMode.Auto
            ? configured
            : providerName == PostgresProvider ? OutboxSchemaMode.Migrate : OutboxSchemaMode.EnsureCreated;
}

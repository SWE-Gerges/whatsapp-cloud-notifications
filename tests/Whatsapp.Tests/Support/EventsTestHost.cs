using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Whatsapp.CloudApi;
using Whatsapp.Events;
using Whatsapp.Outbox;
using Whatsapp.Templates;

namespace Whatsapp.Tests.Support;

/// <summary>
/// The production composition (Cloud API client, templates, outbox, events) on SQLite, with the HTTP transport
/// replaced by a recording stub so tests can prove that no request is made.
/// </summary>
internal sealed class EventsTestHost : IAsyncDisposable
{
    private readonly SqliteOutboxDatabase _database = new();
    private readonly ServiceProvider _provider;

    public EventsTestHost(Dictionary<string, string?>? extraConfiguration = null, Action<IServiceCollection>? configureServices = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        Http = new StubHttpMessageHandler(Time);

        var values = new Dictionary<string, string?>
        {
            ["Whatsapp:Tenants:tenant-a:PhoneNumberId"] = "100200300",
            ["Whatsapp:Tenants:tenant-a:AccessTokenSecret"] = "tenant-a-token",
            ["Whatsapp:Tenants:tenant-a:BaseUrl"] = "https://graph.example.com/v1",
            ["Secrets:tenant-a-token"] = TestData.Token,
        };
        foreach (var (key, value) in extraConfiguration ?? [])
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddWhatsappCloudApi(configuration);
        services.AddWhatsappTemplates(configuration);
        services.AddWhatsappOutbox(configuration, options => options.UseSqlite(_database.ConnectionString));
        services.AddWhatsappEvents(configuration);
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = Http));
        configureServices?.Invoke(services);

        Services = services;
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public FakeTimeProvider Time { get; }

    public StubHttpMessageHandler Http { get; }

    public IServiceCollection Services { get; }

    public IServiceProvider Provider => _provider;

    public SqliteOutboxDatabase Database => _database;

    public Task PublishAsync(INotification notification) =>
        _provider.GetRequiredService<IPublisher>().Publish(notification, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _database.Dispose();
    }
}

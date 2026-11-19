using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;
using Whatsapp.Api;
using Whatsapp.Outbox;

namespace Whatsapp.Tests.Support;

/// <summary>
/// The real API host on SQLite with a fake clock and a recording Cloud API stub. The dispatcher is off by default,
/// so tests drive delivery explicitly through <see cref="ProcessOutboxAsync"/>.
/// </summary>
internal sealed class ApiFactory : WebApplicationFactory<ApiEntryPoint>
{
    public const string AdminKey = "test-admin-key-0123456789";
    public const string TenantAKey = "test-tenant-a-key-0123456";
    public const string TenantBKey = "test-tenant-b-key-0123456";
    public const string TenantAPhoneNumberId = "100200300";
    public const string TenantBPhoneNumberId = "400500600";
    public const string TenantAAppSecret = "app-secret-a-for-tests-only";
    public const string TenantBAppSecret = "app-secret-b-for-tests-only";
    public const string TenantAVerifyToken = "verify-token-a-for-tests-only";
    public const string TenantBVerifyToken = "verify-token-b-for-tests-only";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"api-tests-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, string?> _overrides;
    private readonly Action<IServiceCollection>? _configureServices;

    public ApiFactory(Dictionary<string, string?>? overrides = null, Action<IServiceCollection>? configureServices = null)
    {
        _overrides = overrides ?? [];
        _configureServices = configureServices;
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        Graph = new StubHttpMessageHandler(Time);
    }

    public FakeTimeProvider Time { get; }

    public StubHttpMessageHandler Graph { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={_databasePath}",
                ["Api:AdminKey"] = AdminKey,
                ["Api:TenantKeys:0:TenantId"] = "tenant-a",
                ["Api:TenantKeys:0:Key"] = TenantAKey,
                ["Api:TenantKeys:1:TenantId"] = "tenant-b",
                ["Api:TenantKeys:1:Key"] = TenantBKey,
                ["Whatsapp:Tenants:tenant-a:PhoneNumberId"] = "100200300",
                ["Whatsapp:Tenants:tenant-a:AccessTokenSecret"] = "tenant-a-token",
                ["Whatsapp:Tenants:tenant-a:BaseUrl"] = "https://graph.example.com/v1",
                ["Whatsapp:Tenants:tenant-b:PhoneNumberId"] = "400500600",
                ["Whatsapp:Tenants:tenant-b:AccessTokenSecret"] = "tenant-b-token",
                ["Whatsapp:Tenants:tenant-b:BaseUrl"] = "https://graph.example.com/v1",
                ["Whatsapp:Tenants:tenant-a:WebhookAppSecretName"] = "tenant-a-app-secret",
                ["Whatsapp:Tenants:tenant-a:WebhookVerifyTokenName"] = "tenant-a-verify-token",
                ["Whatsapp:Tenants:tenant-b:WebhookAppSecretName"] = "tenant-b-app-secret",
                ["Whatsapp:Tenants:tenant-b:WebhookVerifyTokenName"] = "tenant-b-verify-token",
                ["Secrets:tenant-a-app-secret"] = TenantAAppSecret,
                ["Secrets:tenant-b-app-secret"] = TenantBAppSecret,
                ["Secrets:tenant-a-verify-token"] = TenantAVerifyToken,
                ["Secrets:tenant-b-verify-token"] = TenantBVerifyToken,
                ["Secrets:tenant-a-token"] = "token-a-for-tests-only",
                ["Secrets:tenant-b-token"] = "token-b-for-tests-only",
                ["Whatsapp:Outbox:DispatcherEnabled"] = "false",
                ["Whatsapp:Outbox:MaxAttempts"] = "3",
                ["Whatsapp:Reconciliation:Enabled"] = "false",
            };
            foreach (var (key, value) in _overrides)
            {
                values[key] = value;
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = Graph));
            _configureServices?.Invoke(services);
        });
    }

    public HttpClient CreateClientWithKey(string? key)
    {
        var client = CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    public HttpClient TenantA() => CreateClientWithKey(TenantAKey);

    public HttpClient TenantB() => CreateClientWithKey(TenantBKey);

    public HttpClient Admin() => CreateClientWithKey(AdminKey);

    public async Task<int> ProcessOutboxAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(TestContext.Current.CancellationToken);
    }

    public Task<ReconciliationResult> ReconcileAsync() =>
        Services.GetRequiredService<ReconciliationProcessor>().RunOnceAsync(TestContext.Current.CancellationToken);

    public List<InboundMessageRecord> InboundMessages()
    {
        using var db = Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OutboxDbContext>>().CreateDbContext();
        return [.. db.InboundMessages.OrderBy(x => x.ReceivedAt).ThenBy(x => x.MessageId)];
    }

    public List<DeliveryReceipt> Receipts()
    {
        using var db = Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OutboxDbContext>>().CreateDbContext();
        return [.. db.DeliveryReceipts.OrderBy(x => x.OccurredAt).ThenBy(x => x.Status)];
    }

    public List<ConversationWindow> Windows()
    {
        using var db = Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OutboxDbContext>>().CreateDbContext();
        return [.. db.ConversationWindows.OrderBy(x => x.Recipient)];
    }

    /// <summary>Posts a webhook body exactly as given, signed with the secret (or unsigned when no secret is given).</summary>
    public static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string body, string? secret = TenantAAppSecret, string? signatureHeader = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/whatsapp") { Content = content };
        var signature = signatureHeader ?? (secret is null ? null : Whatsapp.Webhooks.WebhookSignature.Sign(secret, bytes));
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation(Whatsapp.Webhooks.WebhookSignature.HeaderName, signature);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public List<NotificationLog> Rows()
    {
        using var db = Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OutboxDbContext>>().CreateDbContext();
        return [.. db.NotificationLogs.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)];
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Json, TestContext.Current.CancellationToken);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

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
}

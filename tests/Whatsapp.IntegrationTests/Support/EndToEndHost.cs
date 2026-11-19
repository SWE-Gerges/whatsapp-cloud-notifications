using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Data.Sqlite;
using Whatsapp.Api;
using Whatsapp.FakeGraph;

namespace Whatsapp.IntegrationTests.Support;

/// <summary>
/// The whole service wired together: the API with its background dispatcher on a SQLite database file,
/// talking over (in-memory) HTTP to the fake Graph server.
/// </summary>
internal sealed class EndToEndHost : IAsyncDisposable
{
    public const string AdminKey = "e2e-admin-key-0123456789";
    public const string TenantAKey = "e2e-tenant-a-key-012345";
    public const string DisabledTenantKey = "e2e-disabled-key-012345";
    public const string ExpiredTenantKey = "e2e-expired-key-0123456";
    public const string Recipient = "+12025550100";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly WebApplicationFactory<FakeGraphEntryPoint> _graphFactory;
    private readonly WebApplicationFactory<ApiEntryPoint> _apiFactory;

    private readonly string _databasePath;

    private EndToEndHost(WebApplicationFactory<FakeGraphEntryPoint> graph, WebApplicationFactory<ApiEntryPoint> api, string databasePath)
    {
        _databasePath = databasePath;
        _graphFactory = graph;
        _apiFactory = api;
        Graph = graph.CreateClient();
        TenantA = Client(TenantAKey);
        Admin = Client(AdminKey);
    }

    public HttpClient Graph { get; }

    public HttpClient TenantA { get; }

    public HttpClient Admin { get; }

    public IServiceProvider ApiServices => _apiFactory.Services;

    public const string TenantAPhoneNumberId = "100200300";
    public const string AppSecret = "e2e-app-secret-for-tests-only";

    /// <summary>
    /// The fake Graph server posts its webhooks to the API, which does not exist yet when the fake is built. The fake's
    /// webhook client is given this handler, and the API's in-memory handler is plugged in once the API has started.
    /// </summary>
    private sealed class ForwardingHandler : HttpMessageHandler
    {
        public HttpMessageHandler? Inner { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new HttpMessageInvoker(Inner ?? throw new InvalidOperationException("The API is not running yet."), disposeHandler: false)
                .SendAsync(request, cancellationToken);
    }

    /// <param name="overrides">Settings of the API. Keys starting with <c>FakeGraph:</c> configure the fake Graph server instead.</param>
    public static Task<EndToEndHost> StartAsync(Dictionary<string, string?>? overrides = null)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"e2e-tests-{Guid.NewGuid():N}.db");
        var callbackHandler = new ForwardingHandler();

        var graph = new WebApplicationFactory<FakeGraphEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["FakeGraph:Webhook:CallbackUrl"] = "http://api.test/api/v1/webhooks/whatsapp",
                    ["FakeGraph:Webhook:AppSecret"] = AppSecret,
                    ["FakeGraph:Webhook:SentDelay"] = "00:00:00",
                    ["FakeGraph:Webhook:DeliveredDelay"] = "00:00:00",
                    ["FakeGraph:Webhook:ReadDelay"] = "00:00:00",
                };
                foreach (var (key, value) in overrides ?? [])
                {
                    if (key.StartsWith("FakeGraph:", StringComparison.Ordinal))
                    {
                        values[key] = value;
                    }
                }

                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureTestServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = callbackHandler)));
        });
        var graphHandler = graph.Server.CreateHandler();

        var api = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:ConnectionString"] = $"Data Source={databasePath}",
                    ["Api:AdminKey"] = AdminKey,
                    ["Api:TenantKeys:0:TenantId"] = "tenant-a",
                    ["Api:TenantKeys:0:Key"] = TenantAKey,
                    ["Api:TenantKeys:1:TenantId"] = "tenant-disabled",
                    ["Api:TenantKeys:1:Key"] = DisabledTenantKey,
                    ["Api:TenantKeys:2:TenantId"] = "tenant-expired",
                    ["Api:TenantKeys:2:Key"] = ExpiredTenantKey,
                    ["Whatsapp:Tenants:tenant-a:PhoneNumberId"] = "100200300",
                    ["Whatsapp:Tenants:tenant-a:AccessTokenSecret"] = "token-a",
                    ["Whatsapp:Tenants:tenant-a:BaseUrl"] = "http://fakegraph.test/v1",
                    ["Whatsapp:Tenants:tenant-a:WebhookAppSecretName"] = "app-a",
                    ["Whatsapp:Tenants:tenant-a:WebhookVerifyTokenName"] = "verify-a",
                    ["Secrets:app-a"] = AppSecret,
                    ["Secrets:verify-a"] = "e2e-verify-token-for-tests-only",
                    ["Whatsapp:Reconciliation:PollInterval"] = "00:00:00.100",
                    ["Whatsapp:Reconciliation:GracePeriod"] = "00:00:01",
                    ["Whatsapp:Tenants:tenant-disabled:Enabled"] = "false",
                    ["Whatsapp:Tenants:tenant-expired:PhoneNumberId"] = "700800900",
                    ["Whatsapp:Tenants:tenant-expired:AccessTokenSecret"] = "token-expired",
                    ["Whatsapp:Tenants:tenant-expired:BaseUrl"] = "http://fakegraph.test/v1",
                    ["Secrets:token-a"] = "e2e-valid-token",
                    ["Secrets:token-expired"] = "dev-only-expired-token",
                    ["Whatsapp:Client:RetryBaseDelay"] = "00:00:00.010",
                    ["Whatsapp:Outbox:PollInterval"] = "00:00:00.050",
                    ["Whatsapp:Outbox:RetryBaseDelay"] = "00:00:00.300",
                    ["Whatsapp:Outbox:RetryMaxDelay"] = "00:00:01",
                    ["Whatsapp:Outbox:MaxAttempts"] = "4",
                };
                foreach (var (key, value) in overrides ?? [])
                {
                    if (!key.StartsWith("FakeGraph:", StringComparison.Ordinal))
                    {
                        values[key] = value;
                    }
                }

                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureTestServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = graphHandler)));
        });

        // Starting the API creates the schema before the dispatcher polls; only then can the fake reach it.
        callbackHandler.Inner = api.Server.CreateHandler();
        return Task.FromResult(new EndToEndHost(graph, api, databasePath));
    }

    /// <summary>The webhooks the fake posted (or tried to), oldest first.</summary>
    public async Task<List<JsonElement>> WebhooksAsync()
    {
        var response = await Graph.GetAsync("/_inspect/webhooks", TestContext.Current.CancellationToken);
        return (await ReadAsync(response)).EnumerateArray().ToList();
    }

    /// <summary>Tells the fake how the next messages to a recipient end: <c>read</c>, <c>delivered</c>, <c>sent</c>, <c>failed</c> or <c>none</c>.</summary>
    public async Task SetOutcomeAsync(string final, string recipient = Recipient, int? errorCode = null, string? errorTitle = null, int? remaining = null)
    {
        var response = await PostAsync(Graph, "/_inspect/outcomes", new { recipient, final, errorCode, errorTitle, remaining });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The fake posts a signed inbound message webhook to the API, as if the customer had written to the business number.</summary>
    public async Task SimulateInboundAsync(string from = Recipient)
    {
        var response = await PostAsync(Graph, "/_inspect/inbound", new { from, phoneNumberId = TenantAPhoneNumberId });
        response.EnsureSuccessStatusCode();
    }

    public HttpClient Client(string key)
    {
        var client = _apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    public Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Json, TestContext.Current.CancellationToken);

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);

    public async Task<JsonElement> NotificationAsync(Guid id)
    {
        var response = await Admin.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response);
    }

    public async Task<JsonElement> WaitForStatusAsync(Guid id, string status)
    {
        return await Eventually.Async(
            () => NotificationAsync(id),
            element => element.GetProperty("status").GetString() == status);
    }

    public async Task<List<JsonElement>> GraphMessagesAsync()
    {
        var response = await Graph.GetAsync("/_inspect/messages", TestContext.Current.CancellationToken);
        return (await ReadAsync(response)).EnumerateArray().ToList();
    }

    public async Task<List<JsonElement>> GraphRequestsAsync()
    {
        var response = await Graph.GetAsync("/_inspect/requests", TestContext.Current.CancellationToken);
        return (await ReadAsync(response)).EnumerateArray().ToList();
    }

    public async Task InjectFailureAsync(string mode, int remaining, string? recipient = null)
    {
        var response = await PostAsync(Graph, "/_inspect/failures", new { mode, remaining, recipient });
        response.EnsureSuccessStatusCode();
    }

    public static object BookingEvent(string bookingId = "bk-1001", string status = "Confirmed", string? language = "en", bool optedIn = true) => new
    {
        bookingId,
        reference = "REF-1001",
        customerFirstName = "Sam",
        phoneCountryCallingCode = "1",
        phoneNationalNumber = "2025550100",
        bookingType = "Hotel",
        status,
        details = new { checkInDate = "2026-03-14" },
        companyName = "Acme Travel",
        createdAt = "2026-03-01T09:30:00Z",
        preferredLanguage = language,
        whatsappUpdatesEnabled = optedIn,
    };

    public async ValueTask DisposeAsync()
    {
        Graph.Dispose();
        TenantA.Dispose();
        Admin.Dispose();
        await _apiFactory.DisposeAsync();
        await _graphFactory.DisposeAsync();
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

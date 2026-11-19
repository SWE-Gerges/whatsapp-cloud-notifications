using System.Net;
using System.Text.Json;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Api;

/// <summary>The unconfirmed path through the real HTTP client and the API: a dropped answer, a receipt, the reconciler, a retry.</summary>
public sealed class UnconfirmedApiTests : IDisposable
{
    private const string FixtureNotificationId = "11111111-2222-3333-4444-555555555555";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static HttpRequestException ConnectionDropped() => new(HttpRequestError.ResponseEnded, "The response ended prematurely.");

    private async Task<Guid> EnqueueAsync()
    {
        using var client = _factory.TenantA();
        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "Template",
            eventKey = "order-update",
            referenceKey = "u-1",
            template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });
        return (await ApiFactory.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetAsync(Guid id)
    {
        using var admin = _factory.Admin();
        return await ApiFactory.ReadJsonAsync(await admin.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_dropped_answer_leaves_the_notification_unconfirmed_without_an_inline_retry()
    {
        _factory.Graph.Throw(ConnectionDropped()).Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var id = await EnqueueAsync();

        await _factory.ProcessOutboxAsync();
        _factory.Time.Advance(TimeSpan.FromHours(1));
        await _factory.ProcessOutboxAsync();

        var body = await GetAsync(id);
        body.GetProperty("status").GetString().ShouldBe("Unconfirmed");
        body.GetProperty("error").GetString().ShouldStartWith("Unconfirmed: Network:");
        _factory.Graph.Requests.Count.ShouldBe(1);

        using var json = JsonDocument.Parse(_factory.Graph.Requests[0].Body);
        json.RootElement.GetProperty("biz_opaque_callback_data").GetString().ShouldBe(id.ToString("D"));
    }

    [Fact]
    public async Task A_signed_status_webhook_that_echoes_the_id_confirms_it()
    {
        _factory.Graph.Throw(ConnectionDropped());
        var id = await EnqueueAsync();
        await _factory.ProcessOutboxAsync();

        using var client = _factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook("status-sent.json", (FixtureNotificationId, id.ToString()));
        (await ApiFactory.PostWebhookAsync(client, body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var notification = await GetAsync(id);
        notification.GetProperty("status").GetString().ShouldBe("Sent");
        notification.GetProperty("providerMessageId").GetString().ShouldBe("wamid.OUTBOUND-1");
        notification.GetProperty("deliveryStatus").GetString().ShouldBe("Sent");
        _factory.Graph.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_a_receipt_the_reconciler_dead_letters_it_and_an_administrator_can_retry_it()
    {
        _factory.Graph.Throw(ConnectionDropped());
        var id = await EnqueueAsync();
        await _factory.ProcessOutboxAsync();

        _factory.Time.Advance(TimeSpan.FromMinutes(15));
        (await _factory.ReconcileAsync()).DeadLettered.ShouldBe(1);
        var dead = await GetAsync(id);
        dead.GetProperty("status").GetString().ShouldBe("DeadLettered");
        dead.GetProperty("error").GetString().ShouldStartWith("unconfirmed:");

        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        using var admin = _factory.Admin();
        (await ApiFactory.PostAsync(admin, $"/api/v1/notifications/{id}/retry", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.ProcessOutboxAsync();

        (await GetAsync(id)).GetProperty("status").GetString().ShouldBe("Sent");
        _factory.Graph.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unconfirmed_notification_can_be_retried_before_the_grace_period_is_over()
    {
        _factory.Graph.Throw(ConnectionDropped()).Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var id = await EnqueueAsync();
        await _factory.ProcessOutboxAsync();
        using var admin = _factory.Admin();

        (await ApiFactory.PostAsync(admin, $"/api/v1/notifications/{id}/retry", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.ProcessOutboxAsync();

        (await GetAsync(id)).GetProperty("status").GetString().ShouldBe("Sent");
    }

    [Fact]
    public async Task A_failure_that_provably_never_left_is_retried_by_the_outbox_not_unconfirmed()
    {
        _factory.Graph.Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var id = await EnqueueAsync();

        // The client retries a provable connect failure inline, waiting on the fake clock, then the outbox schedules the next attempt.
        await _factory.Time.RunWithTimeAsync(_factory.ProcessOutboxAsync());

        (await GetAsync(id)).GetProperty("status").GetString().ShouldBe("Pending");
        _factory.Graph.Requests.Count.ShouldBe(3, "the first attempt and the two inline retries");
    }

    [Fact]
    public async Task The_reconciliation_policy_is_configuration()
    {
        using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["Whatsapp:Reconciliation:OnGraceExpired"] = "Resend",
            ["Whatsapp:Reconciliation:GracePeriod"] = "00:01:00",
        });
        factory.Graph.Throw(ConnectionDropped()).Respond(HttpStatusCode.OK, TestData.SuccessBody);
        using var tenant = factory.TenantA();
        var created = await ApiFactory.PostAsync(tenant, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "Template",
            eventKey = "order-update",
            referenceKey = "u-2",
            template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });
        var id = (await ApiFactory.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        await factory.ProcessOutboxAsync();

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        (await factory.ReconcileAsync()).Requeued.ShouldBe(1);
        await factory.ProcessOutboxAsync();

        using var admin = factory.Admin();
        var row = await ApiFactory.ReadJsonAsync(await admin.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken));
        row.GetProperty("status").GetString().ShouldBe("Sent");
        row.GetProperty("attempts").GetInt32().ShouldBe(2);
    }

    [Theory]
    [InlineData("Whatsapp:Reconciliation:OnGraceExpired", "Sometimes")]
    [InlineData("Whatsapp:Reconciliation:PollInterval", "00:00:00")]
    [InlineData("Whatsapp:Reconciliation:GracePeriod", "-00:00:01")]
    public void Invalid_reconciliation_settings_stop_the_host_at_startup(string key, string value)
    {
        using var factory = new ApiFactory(new Dictionary<string, string?> { [key] = value });

        Should.Throw<Exception>(() => factory.CreateClient());
    }
}

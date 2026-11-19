using System.Net;
using System.Text.Json;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Api;

/// <summary>Delivery receipts through the signed webhook, and what the notification endpoints then report.</summary>
public sealed class ReceiptApiTests : IDisposable
{
    private const string FixtureNotificationId = "11111111-2222-3333-4444-555555555555";
    private const string FixtureMessageId = "wamid.OUTBOUND-1";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SendTemplateAsync(string reference = "r-1", string providerMessageId = "wamid.TEST-1")
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody.Replace("wamid.TEST-1", providerMessageId, StringComparison.Ordinal));
        using var client = _factory.TenantA();
        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "Template",
            eventKey = "order-update",
            referenceKey = reference,
            template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });
        var id = (await ApiFactory.ReadJsonAsync(response)).GetProperty("id").GetGuid();
        await _factory.ProcessOutboxAsync();
        return id;
    }

    private async Task PostStatusAsync(string fixture, Guid notificationId, string providerMessageId = "wamid.TEST-1")
    {
        using var client = _factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook(fixture, (FixtureNotificationId, notificationId.ToString()), (FixtureMessageId, providerMessageId));
        (await ApiFactory.PostWebhookAsync(client, body)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<JsonElement> GetAsync(Guid id, HttpClient? client = null)
    {
        using var admin = _factory.Admin();
        var response = await (client ?? admin).GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await ApiFactory.ReadJsonAsync(response);
    }

    [Fact]
    public async Task A_notification_reports_its_receipt_timeline_and_derived_delivery_status()
    {
        var id = await SendTemplateAsync();

        await PostStatusAsync("status-delivered.json", id);
        await PostStatusAsync("status-sent.json", id);
        await PostStatusAsync("status-read.json", id);

        using var tenant = _factory.TenantA();
        var body = await GetAsync(id, tenant);
        body.GetProperty("status").GetString().ShouldBe("Sent");
        body.GetProperty("deliveryStatus").GetString().ShouldBe("Read");
        var timeline = body.GetProperty("receipts").EnumerateArray().ToList();
        timeline.Select(r => r.GetProperty("status").GetString()).ShouldBe(["sent", "delivered", "read"]);
        timeline.ShouldAllBe(r => r.GetProperty("providerMessageId").GetString() == "wamid.TEST-1");
        timeline.Select(r => r.GetProperty("occurredAt").GetDateTimeOffset()).ShouldBe(timeline.Select(r => r.GetProperty("occurredAt").GetDateTimeOffset()).Order());
    }

    [Fact]
    public async Task A_notification_without_receipts_has_no_delivery_status_and_an_empty_timeline()
    {
        var id = await SendTemplateAsync();

        var body = await GetAsync(id);

        body.TryGetProperty("deliveryStatus", out _).ShouldBeFalse();
        body.GetProperty("receipts").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_the_notification_or_its_receipts()
    {
        var id = await SendTemplateAsync();
        await PostStatusAsync("status-delivered.json", id);
        using var other = _factory.TenantB();

        (await other.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_list_filters_by_delivery_status_and_shows_it_on_every_item()
    {
        var read = await SendTemplateAsync("r-read", "wamid.READ");
        var delivered = await SendTemplateAsync("r-delivered", "wamid.DELIVERED");
        await SendTemplateAsync("r-none", "wamid.NONE");
        await PostStatusAsync("status-read.json", read, "wamid.READ");
        await PostStatusAsync("status-delivered.json", delivered, "wamid.DELIVERED");
        using var admin = _factory.Admin();

        var onlyRead = await ApiFactory.ReadJsonAsync(await admin.GetAsync("/api/v1/notifications?deliveryStatus=Read", TestContext.Current.CancellationToken));
        var onlyDelivered = await ApiFactory.ReadJsonAsync(await admin.GetAsync("/api/v1/notifications?deliveryStatus=Delivered", TestContext.Current.CancellationToken));
        var everything = await ApiFactory.ReadJsonAsync(await admin.GetAsync("/api/v1/notifications", TestContext.Current.CancellationToken));

        onlyRead.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldBe([read]);
        onlyDelivered.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldBe([delivered]);
        everything.GetProperty("total").GetInt32().ShouldBe(3);
        everything.GetProperty("items").EnumerateArray().Count(i => i.TryGetProperty("deliveryStatus", out _)).ShouldBe(2);
    }

    [Fact]
    public async Task An_unknown_delivery_status_filter_is_rejected()
    {
        using var admin = _factory.Admin();

        (await admin.GetAsync("/api/v1/notifications?deliveryStatus=Nope", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_failed_receipt_marks_the_notification_failed_with_the_reason()
    {
        var id = await SendTemplateAsync();

        await PostStatusAsync("status-failed.json", id);

        var body = await GetAsync(id);
        body.GetProperty("status").GetString().ShouldBe("Failed");
        body.GetProperty("deliveryStatus").GetString().ShouldBe("Failed");
        body.GetProperty("error").GetString().ShouldBe("Delivery failed: 131047 Re-engagement message");
        var failure = body.GetProperty("receipts")[0];
        failure.GetProperty("errorCode").GetInt32().ShouldBe(131047);
        failure.GetProperty("errorTitle").GetString().ShouldBe("Re-engagement message");
    }

    [Fact]
    public async Task A_window_failure_on_free_text_leads_to_exactly_one_template_message()
    {
        await PostInboundAsync();
        _factory.Graph
            .Respond(HttpStatusCode.OK, TestData.SuccessBody)
            .Respond(HttpStatusCode.OK, TestData.SuccessBody.Replace("wamid.TEST-1", "wamid.TEST-2", StringComparison.Ordinal));
        using var client = _factory.TenantA();
        var created = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "FreeText",
            eventKey = "custom",
            referenceKey = "ft-1",
            text = "Your table is ready.",
            template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });
        var id = (await ApiFactory.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        await _factory.ProcessOutboxAsync();

        // The provider disagrees about the window and reports it through the receipt, twice.
        await PostStatusAsync("status-failed.json", id);
        await PostStatusAsync("status-failed.json", id);
        await _factory.ProcessOutboxAsync();
        await _factory.ProcessOutboxAsync();

        var body = await GetAsync(id);
        body.GetProperty("status").GetString().ShouldBe("Failed");
        var fallbackId = body.GetProperty("fallbackNotificationId").GetGuid();
        var fallback = await GetAsync(fallbackId);
        fallback.GetProperty("status").GetString().ShouldBe("Sent");
        fallback.GetProperty("kind").GetString().ShouldBe("Template");
        _factory.Graph.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("type").GetString())
            .ShouldBe(["text", "template"]);
    }

    private async Task PostInboundAsync()
    {
        using var client = _factory.CreateClientWithKey(null);
        (await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

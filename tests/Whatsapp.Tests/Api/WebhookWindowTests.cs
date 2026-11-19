using System.Net;
using System.Text.Json;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Api;

/// <summary>The 24 hour service window is opened by real inbound messages, delivered through the signed webhook.</summary>
public sealed class WebhookWindowTests : IDisposable
{
    private const string InboundTimestamp = "1772359200";
    private const string InboundMessageId = "wamid.INBOUND-TEXT-1";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static object FreeText(string reference, bool withFallback = true) => new
    {
        recipient = TestData.Recipient,
        kind = "FreeText",
        eventKey = "custom",
        referenceKey = reference,
        text = "Your table is ready.",
        template = withFallback
            ? new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } }
            : null,
    };

    private async Task<Guid> EnqueueAsync(object body)
    {
        using var client = _factory.TenantA();
        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await ApiFactory.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetAsync(Guid id)
    {
        using var admin = _factory.Admin();
        return await ApiFactory.ReadJsonAsync(await admin.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken));
    }

    private async Task PostInboundAsync(string timestamp = InboundTimestamp, string messageId = InboundMessageId)
    {
        using var client = _factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook("inbound-text.json", (InboundTimestamp, timestamp), (InboundMessageId, messageId));
        (await ApiFactory.PostWebhookAsync(client, body)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private string LastGraphMessageType() =>
        JsonDocument.Parse(_factory.Graph.Requests[^1].Body).RootElement.GetProperty("type").GetString()!;

    [Fact]
    public async Task A_signed_inbound_webhook_opens_the_window_so_free_text_is_sent_as_text()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        await PostInboundAsync();
        var id = await EnqueueAsync(FreeText("w-1"));

        await _factory.ProcessOutboxAsync();

        (await GetAsync(id)).GetProperty("status").GetString().ShouldBe("Sent");
        _factory.Graph.Requests.Count.ShouldBe(1);
        LastGraphMessageType().ShouldBe("text");
    }

    [Fact]
    public async Task Without_an_inbound_message_the_same_request_falls_back_to_its_template()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var id = await EnqueueAsync(FreeText("w-1"));

        await _factory.ProcessOutboxAsync();

        (await GetAsync(id)).GetProperty("status").GetString().ShouldBe("Sent");
        LastGraphMessageType().ShouldBe("template");
    }

    [Fact]
    public async Task A_closed_window_without_a_fallback_template_is_skipped_as_window_closed()
    {
        var id = await EnqueueAsync(FreeText("w-1", withFallback: false));

        await _factory.ProcessOutboxAsync();

        var row = await GetAsync(id);
        row.GetProperty("status").GetString().ShouldBe("Skipped");
        row.GetProperty("error").GetString().ShouldBe("window closed");
        _factory.Graph.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_window_expires_exactly_24_hours_after_the_timestamp_of_the_message()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        await PostInboundAsync();

        _factory.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        var inside = await EnqueueAsync(FreeText("inside"));
        await _factory.ProcessOutboxAsync();

        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var outside = await EnqueueAsync(FreeText("outside"));
        await _factory.ProcessOutboxAsync();

        (await GetAsync(inside)).GetProperty("status").GetString().ShouldBe("Sent");
        (await GetAsync(outside)).GetProperty("status").GetString().ShouldBe("Sent");
        _factory.Graph.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("type").GetString())
            .ShouldBe(["text", "template"]);
    }

    [Fact]
    public async Task The_window_follows_the_timestamp_of_the_payload_not_the_time_of_arrival()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        _factory.Time.Advance(TimeSpan.FromHours(30));

        // Delivered 30 hours late (the provider retried for a day): the message is already outside its own window.
        await PostInboundAsync();
        var id = await EnqueueAsync(FreeText("late"));
        await _factory.ProcessOutboxAsync();

        (await GetAsync(id)).GetProperty("status").GetString().ShouldBe("Sent");
        LastGraphMessageType().ShouldBe("template");
        _factory.Windows().ShouldHaveSingleItem().LastInboundAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(long.Parse(InboundTimestamp)));
    }

    [Fact]
    public async Task The_window_only_moves_forward_when_messages_arrive_out_of_order()
    {
        var start = long.Parse(InboundTimestamp);
        await PostInboundAsync((start + 3600).ToString(System.Globalization.CultureInfo.InvariantCulture), "wamid.NEWER");
        await PostInboundAsync((start - 7200).ToString(System.Globalization.CultureInfo.InvariantCulture), "wamid.OLDER");
        await PostInboundAsync((start + 3600).ToString(System.Globalization.CultureInfo.InvariantCulture), "wamid.NEWER");

        _factory.InboundMessages().Count.ShouldBe(2);
        _factory.Windows().ShouldHaveSingleItem().LastInboundAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(start + 3600));
    }

    [Fact]
    public async Task The_demo_endpoint_and_the_webhook_leave_the_same_window_state()
    {
        // Same instant, same sender, one tenant each: one message simulated by the admin route, one delivered signed.
        using var admin = _factory.Admin();
        var simulated = await ApiFactory.PostAsync(admin, "/api/v1/admin/conversations/inbound", new
        {
            tenantId = "tenant-a",
            recipient = TestData.Recipient,
            receivedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(InboundTimestamp)),
        });
        simulated.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var client = _factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook("inbound-text.json", ("100200300", ApiFactory.TenantBPhoneNumberId));
        (await ApiFactory.PostWebhookAsync(client, body, secret: ApiFactory.TenantBAppSecret)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var windows = _factory.Windows().OrderBy(w => w.TenantId).ToList();
        windows.Select(w => (w.TenantId, w.Recipient, w.LastInboundAt)).ShouldBe(
        [
            ("tenant-a", TestData.Recipient, DateTimeOffset.FromUnixTimeSeconds(long.Parse(InboundTimestamp))),
            ("tenant-b", TestData.Recipient, DateTimeOffset.FromUnixTimeSeconds(long.Parse(InboundTimestamp))),
        ]);

        var messages = _factory.InboundMessages().OrderBy(m => m.TenantId).ToList();
        messages.Select(m => m.Type).ShouldBe(["simulated", "text"]);
        messages.Select(m => m.ReceivedAt).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task The_message_body_is_discarded_and_never_reaches_the_database()
    {
        await PostInboundAsync();

        var message = _factory.InboundMessages().ShouldHaveSingleItem();
        JsonSerializer.Serialize(message).ShouldNotContain("Hello, is my booking confirmed?");
        typeof(Whatsapp.Outbox.InboundMessageRecord).GetProperties().Select(p => p.Name).ShouldBe(
            ["Id", "TenantId", "MessageId", "Sender", "Type", "ReceivedAt", "RecordedAt"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_sender_is_normalised_through_the_same_e164_path_as_outbound_numbers()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        await PostInboundAsync();

        // The webhook sends digits only; the window is keyed by the E.164 form the notification uses.
        _factory.Windows().ShouldHaveSingleItem().Recipient.ShouldBe(TestData.Recipient);
    }
}

using System.Net;
using System.Text.Json;
using Whatsapp.IntegrationTests.Support;

namespace Whatsapp.IntegrationTests;

/// <summary>
/// The whole loop with no Docker and no live API: the service sends through its real HTTP client to the fake Graph server,
/// and the fake answers with signed webhooks that arrive at the service's real webhook route.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EndToEndWebhookTests
{
    private static object Template(string reference = "o-1") => new
    {
        recipient = EndToEndHost.Recipient,
        kind = "Template",
        eventKey = "order-update",
        referenceKey = reference,
        template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
    };

    private static object FreeTextWithFallback(string reference = "f-1") => new
    {
        recipient = EndToEndHost.Recipient,
        kind = "FreeText",
        eventKey = "order-update",
        referenceKey = reference,
        text = "Your table is ready.",
        template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
    };

    private static async Task<Guid> EnqueueAsync(EndToEndHost host, object body)
    {
        var response = await host.PostAsync(host.TenantA, "/api/v1/notifications", body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await EndToEndHost.ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<JsonElement> WaitForDeliveryAsync(EndToEndHost host, Guid id, string delivery) =>
        Eventually.Async(
            () => host.NotificationAsync(id),
            n => n.TryGetProperty("deliveryStatus", out var d) && d.GetString() == delivery);

    [Fact]
    public async Task The_status_webhooks_of_the_fake_build_the_receipt_timeline_through_the_real_webhook_route()
    {
        await using var host = await EndToEndHost.StartAsync();

        var id = await EnqueueAsync(host, Template());
        await WaitForDeliveryAsync(host, id, "Read");

        // The fake answers instantly, so the receipts can overtake the dispatcher's commit: the row is Sent a moment later.
        var notification = await host.WaitForStatusAsync(id, "Sent");

        notification.GetProperty("deliveryStatus").GetString().ShouldBe("Read");
        notification.GetProperty("receipts").EnumerateArray().Select(r => r.GetProperty("status").GetString())
            .ShouldBe(["sent", "delivered", "read"], ignoreOrder: true);
        var messageId = (await host.GraphMessagesAsync()).ShouldHaveSingleItem().GetProperty("messageId").GetString();
        notification.GetProperty("providerMessageId").GetString().ShouldBe(messageId);
        (await host.WebhooksAsync()).ShouldAllBe(w => w.GetProperty("httpStatus").GetInt32() == 200);
    }

    [Fact]
    public async Task The_fake_echoes_the_notification_id_so_the_service_finds_it_in_every_receipt()
    {
        await using var host = await EndToEndHost.StartAsync();

        var id = await EnqueueAsync(host, Template());
        await WaitForDeliveryAsync(host, id, "Read");

        (await host.WebhooksAsync()).ShouldAllBe(w => w.GetProperty("callbackData").GetString() == id.ToString("D"));
        (await host.GraphRequestsAsync()).ShouldHaveSingleItem().GetProperty("callbackData").GetString().ShouldBe(id.ToString("D"));
    }

    [Fact]
    public async Task An_inbound_message_from_the_fake_opens_the_window_through_the_real_webhook_path()
    {
        await using var host = await EndToEndHost.StartAsync();
        var freeText = new
        {
            recipient = EndToEndHost.Recipient,
            kind = "FreeText",
            eventKey = "order-update",
            referenceKey = "f-1",
            text = "Your order is ready.",
        };
        var closed = await EndToEndHost.ReadAsync(await host.PostAsync(host.TenantA, "/api/v1/notifications", freeText));
        (await host.WaitForStatusAsync(closed.GetProperty("id").GetGuid(), "Skipped")).GetProperty("error").GetString().ShouldBe("window closed");

        await host.SimulateInboundAsync();

        var open = await EndToEndHost.ReadAsync(await host.PostAsync(host.TenantA, "/api/v1/notifications", freeText with { referenceKey = "f-2" }));
        await host.WaitForStatusAsync(open.GetProperty("id").GetGuid(), "Sent");
        var message = (await host.GraphMessagesAsync()).ShouldHaveSingleItem();
        message.GetProperty("type").GetString().ShouldBe("text");
        (await host.WebhooksAsync()).Count(w => w.GetProperty("kind").GetString() == "inbound").ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_receipt_from_the_fake_marks_the_notification_failed_and_it_is_not_sent_again()
    {
        await using var host = await EndToEndHost.StartAsync();
        await host.SetOutcomeAsync("failed", errorCode: 131026, errorTitle: "Message undeliverable");

        var id = await EnqueueAsync(host, Template());
        var notification = await host.WaitForStatusAsync(id, "Failed");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        notification.GetProperty("error").GetString().ShouldBe("Delivery failed: 131026 Message undeliverable");
        notification.GetProperty("deliveryStatus").GetString().ShouldBe("Failed");
        (await host.GraphMessagesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_window_error_reported_by_a_receipt_leads_to_exactly_one_template_message()
    {
        await using var host = await EndToEndHost.StartAsync();
        await host.SimulateInboundAsync();
        // Only the first message (the free text) is reported as outside the window; its template then reads normally.
        await host.SetOutcomeAsync("failed", errorCode: 131047, errorTitle: "Re-engagement message", remaining: 1);

        var id = await EnqueueAsync(host, FreeTextWithFallback());
        var original = await host.WaitForStatusAsync(id, "Failed");
        var fallbackId = original.GetProperty("fallbackNotificationId").GetGuid();
        var fallback = await host.WaitForStatusAsync(fallbackId, "Sent");
        await Task.Delay(400, TestContext.Current.CancellationToken);

        fallback.GetProperty("kind").GetString().ShouldBe("Template");
        var messages = await host.GraphMessagesAsync();
        messages.Select(m => m.GetProperty("type").GetString()).ShouldBe(["text", "template"]);
        (await EndToEndHost.ReadAsync(await host.Admin.GetAsync("/api/v1/notifications", TestContext.Current.CancellationToken)))
            .GetProperty("total").GetInt32().ShouldBe(2);
    }

    // ---- accept, then drop the answer ----

    [Fact]
    public async Task A_dropped_answer_leaves_the_row_unconfirmed_until_the_receipt_confirms_it_and_one_message_exists()
    {
        // The receipts follow half a second after the acceptance, so the unconfirmed state can be observed first.
        await using var host = await EndToEndHost.StartAsync(new Dictionary<string, string?>
        {
            ["FakeGraph:Webhook:SentDelay"] = "00:00:00.800",
            ["FakeGraph:Webhook:DeliveredDelay"] = "00:00:00.900",
            ["FakeGraph:Webhook:ReadDelay"] = "00:00:01",
            ["Whatsapp:Reconciliation:GracePeriod"] = "00:00:30",
        });
        await host.InjectFailureAsync("drop-after-accept", remaining: 1);

        var id = await EnqueueAsync(host, Template());
        var unconfirmed = await host.WaitForStatusAsync(id, "Unconfirmed");
        unconfirmed.GetProperty("error").GetString().ShouldStartWith("Unconfirmed: Network:");
        unconfirmed.GetProperty("attempts").GetInt32().ShouldBe(1);

        var sent = await host.WaitForStatusAsync(id, "Sent");
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var messages = await host.GraphMessagesAsync();
        var message = messages.ShouldHaveSingleItem();
        message.GetProperty("responseDropped").GetBoolean().ShouldBeTrue();
        sent.GetProperty("providerMessageId").GetString().ShouldBe(message.GetProperty("messageId").GetString());
        (await host.NotificationAsync(id)).GetProperty("attempts").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_dropped_answer_without_any_receipt_is_dead_lettered_and_the_message_is_never_sent_again()
    {
        await using var host = await EndToEndHost.StartAsync();
        await host.SetOutcomeAsync("none");
        await host.InjectFailureAsync("drop-after-accept", remaining: 1);

        var id = await EnqueueAsync(host, Template());
        var dead = await host.WaitForStatusAsync(id, "DeadLettered");
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        dead.GetProperty("error").GetString().ShouldStartWith("unconfirmed:");
        (await host.GraphMessagesAsync()).Count.ShouldBe(1, "at most once: nothing was sent again");
        (await host.NotificationAsync(id)).GetProperty("status").GetString().ShouldBe("DeadLettered");
    }

    [Fact]
    public async Task With_the_at_least_once_policy_the_unconfirmed_message_is_sent_again_and_delivered_twice()
    {
        await using var host = await EndToEndHost.StartAsync(new Dictionary<string, string?>
        {
            ["Whatsapp:Reconciliation:OnGraceExpired"] = "Resend",
        });
        await host.SetOutcomeAsync("none", remaining: 1);
        await host.InjectFailureAsync("drop-after-accept", remaining: 1);

        var id = await EnqueueAsync(host, Template());
        var sent = await host.WaitForStatusAsync(id, "Sent");

        sent.GetProperty("attempts").GetInt32().ShouldBe(2);
        (await host.GraphMessagesAsync()).Count.ShouldBe(2, "the first request had been accepted: the cost of at least once");
    }

    [Fact]
    public async Task An_administrator_can_retry_a_dead_lettered_unconfirmed_notification()
    {
        await using var host = await EndToEndHost.StartAsync();
        await host.SetOutcomeAsync("none", remaining: 1);
        await host.InjectFailureAsync("drop-after-accept", remaining: 1);
        var id = await EnqueueAsync(host, Template());
        await host.WaitForStatusAsync(id, "DeadLettered");

        (await host.PostAsync(host.Admin, $"/api/v1/notifications/{id}/retry", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await host.WaitForStatusAsync(id, "Sent");
        (await host.GraphMessagesAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_replayed_status_webhook_changes_nothing()
    {
        await using var host = await EndToEndHost.StartAsync();
        var id = await EnqueueAsync(host, Template());
        var before = await WaitForDeliveryAsync(host, id, "Read");

        // Posting the same fake deliveries again is a replay as far as the service can tell: the fake re-sends the same ids.
        var receipts = before.GetProperty("receipts").GetArrayLength();
        await host.SimulateInboundAsync();
        await host.SimulateInboundAsync();

        var after = await host.NotificationAsync(id);
        after.GetProperty("receipts").GetArrayLength().ShouldBe(receipts);
        after.GetProperty("deliveryStatus").GetString().ShouldBe("Read");
    }
}

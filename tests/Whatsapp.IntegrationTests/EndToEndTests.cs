using System.Net;
using Whatsapp.IntegrationTests.Support;

namespace Whatsapp.IntegrationTests;

/// <summary>The whole service on SQLite, talking to the fake Graph server through its real HTTP client and outbox.</summary>
[Trait("Category", "Integration")]
public sealed class EndToEndTests
{
    [Fact]
    public async Task A_booking_created_event_reaches_the_graph_exactly_once_even_when_posted_twice()
    {
        await using var host = await EndToEndHost.StartAsync();

        var first = await host.PostAsync(host.TenantA, "/api/v1/events/booking-created", EndToEndHost.BookingEvent());
        var second = await host.PostAsync(host.TenantA, "/api/v1/events/booking-created", EndToEndHost.BookingEvent());
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var messages = await Eventually.Async(host.GraphMessagesAsync, m => m.Count >= 1);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        messages = await host.GraphMessagesAsync();

        messages.Count.ShouldBe(1);
        var message = messages[0];
        message.GetProperty("to").GetString().ShouldBe("+12025550100");
        message.GetProperty("type").GetString().ShouldBe("template");
        message.GetProperty("templateName").GetString().ShouldBe("booking_created");
        message.GetProperty("language").GetString().ShouldBe("en");
        message.GetProperty("parameters").EnumerateArray().Select(p => p.GetString())
            .ShouldBe(["Sam", "Acme Travel", "Hotels", "2026-03-01", "Check-in 2026-03-14"]);
        message.GetProperty("phoneNumberId").GetString().ShouldBe("100200300");

        var list = await EndToEndHost.ReadAsync(await host.Admin.GetAsync("/api/v1/notifications", TestContext.Current.CancellationToken));
        list.GetProperty("total").GetInt32().ShouldBe(1);
        var row = list.GetProperty("items")[0];
        row.GetProperty("status").GetString().ShouldBe("Sent");
        row.GetProperty("providerMessageId").GetString().ShouldBe(message.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task Concurrent_posts_of_one_notification_create_one_row_and_send_one_message()
    {
        await using var host = await EndToEndHost.StartAsync();
        var request = new
        {
            recipient = EndToEndHost.Recipient,
            kind = "Template",
            eventKey = "order-update",
            referenceKey = "o-1",
            template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => host.PostAsync(host.TenantA, "/api/v1/notifications", request)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(31);
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await EndToEndHost.ReadAsync(response)).GetProperty("id").GetGuid());
        }

        ids.Count.ShouldBe(1);
        await host.WaitForStatusAsync(ids.Single(), "Sent");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        (await host.GraphMessagesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_status_change_is_sent_in_the_customer_language_and_skipped_without_opt_in()
    {
        await using var host = await EndToEndHost.StartAsync();

        await host.PostAsync(host.TenantA, "/api/v1/events/booking-status-changed", EndToEndHost.BookingEvent(status: "Cancelled", language: "ar"));
        await host.PostAsync(host.TenantA, "/api/v1/events/booking-status-changed", EndToEndHost.BookingEvent(bookingId: "bk-2", optedIn: false));

        var messages = await Eventually.Async(host.GraphMessagesAsync, m => m.Count >= 1);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        messages = await host.GraphMessagesAsync();

        messages.Count.ShouldBe(1);
        messages[0].GetProperty("templateName").GetString().ShouldBe("booking_status_updated");
        messages[0].GetProperty("language").GetString().ShouldBe("ar");
        messages[0].GetProperty("parameters")[2].GetString().ShouldBe("ملغي");
    }

    [Fact]
    public async Task Free_text_follows_the_service_window_from_skipped_to_sent()
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
        var skipped = await host.WaitForStatusAsync(closed.GetProperty("id").GetGuid(), "Skipped");
        skipped.GetProperty("error").GetString().ShouldBe("window closed");
        (await host.GraphMessagesAsync()).ShouldBeEmpty();

        var inbound = await host.PostAsync(host.Admin, "/api/v1/admin/conversations/inbound", new { tenantId = "tenant-a", recipient = EndToEndHost.Recipient });
        inbound.StatusCode.ShouldBe(HttpStatusCode.OK);

        var open = await EndToEndHost.ReadAsync(await host.PostAsync(host.TenantA, "/api/v1/notifications", freeText with { referenceKey = "f-2" }));
        await host.WaitForStatusAsync(open.GetProperty("id").GetGuid(), "Sent");

        var messages = await host.GraphMessagesAsync();
        messages.Count.ShouldBe(1);
        messages[0].GetProperty("type").GetString().ShouldBe("text");
        messages[0].GetProperty("text").GetString().ShouldBe("Your order is ready.");
    }

    [Fact]
    public async Task Free_text_outside_the_window_falls_back_to_its_template()
    {
        await using var host = await EndToEndHost.StartAsync();

        var created = await EndToEndHost.ReadAsync(await host.PostAsync(host.TenantA, "/api/v1/notifications", new
        {
            recipient = EndToEndHost.Recipient,
            kind = "FreeText",
            eventKey = "order-update",
            referenceKey = "f-1",
            text = "Your booking is confirmed.",
            template = new { name = "booking_status_updated", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        }));
        await host.WaitForStatusAsync(created.GetProperty("id").GetGuid(), "Sent");

        var message = (await host.GraphMessagesAsync()).ShouldHaveSingleItem();
        message.GetProperty("type").GetString().ShouldBe("template");
        message.GetProperty("language").GetString().ShouldBe("en");
    }

    [Fact]
    public async Task Transient_provider_errors_are_retried_by_the_outbox_until_the_message_is_accepted()
    {
        await using var host = await EndToEndHost.StartAsync(new Dictionary<string, string?>
        {
            ["Whatsapp:Client:MaxRetryAttempts"] = "0",
        });
        await host.InjectFailureAsync("server-error", remaining: 2);

        var created = await EndToEndHost.ReadAsync(await host.PostAsync(host.TenantA, "/api/v1/events/booking-created", EndToEndHost.BookingEvent()));
        created.GetProperty("referenceKey").GetString().ShouldBe("bk-1001");

        var list = await Eventually.Async(
            async () => await EndToEndHost.ReadAsync(await host.Admin.GetAsync("/api/v1/notifications?status=Sent", TestContext.Current.CancellationToken)),
            l => l.GetProperty("total").GetInt32() == 1);

        var row = list.GetProperty("items")[0];
        row.GetProperty("attempts").GetInt32().ShouldBe(3);
        var requests = await host.GraphRequestsAsync();
        requests.Select(r => r.GetProperty("outcome").GetString()).ShouldBe(["server-error", "server-error", "accepted"]);
        (await host.GraphMessagesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_client_absorbs_a_short_outage_within_a_single_attempt()
    {
        await using var host = await EndToEndHost.StartAsync();
        await host.InjectFailureAsync("server-error", remaining: 1);

        await host.PostAsync(host.TenantA, "/api/v1/events/booking-created", EndToEndHost.BookingEvent());

        var list = await Eventually.Async(
            async () => await EndToEndHost.ReadAsync(await host.Admin.GetAsync("/api/v1/notifications?status=Sent", TestContext.Current.CancellationToken)),
            l => l.GetProperty("total").GetInt32() == 1);
        list.GetProperty("items")[0].GetProperty("attempts").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_rate_limit_with_retry_after_is_retried_and_succeeds()
    {
        await using var host = await EndToEndHost.StartAsync();
        await host.InjectFailureAsync("rate-limit", remaining: 1);

        var created = await host.PostAsync(host.TenantA, "/api/v1/notifications", new
        {
            recipient = EndToEndHost.Recipient,
            kind = "Template",
            eventKey = "e",
            referenceKey = "r",
            template = new { name = "booking_status_updated", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });
        var id = (await EndToEndHost.ReadAsync(created)).GetProperty("id").GetGuid();

        await host.WaitForStatusAsync(id, "Sent");
        var outcomes = (await host.GraphRequestsAsync()).Select(r => r.GetProperty("outcome").GetString()).ToList();
        outcomes.ShouldBe(["rate-limit", "accepted"]);
    }

    [Fact]
    public async Task A_permanent_error_dead_letters_the_row_and_an_admin_retry_delivers_it()
    {
        await using var host = await EndToEndHost.StartAsync();
        await host.InjectFailureAsync("invalid-recipient", remaining: 1);

        var created = await host.PostAsync(host.TenantA, "/api/v1/notifications", new
        {
            recipient = EndToEndHost.Recipient,
            kind = "Template",
            eventKey = "e",
            referenceKey = "r",
            template = new { name = "booking_status_updated", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });
        var id = (await EndToEndHost.ReadAsync(created)).GetProperty("id").GetGuid();

        var dead = await host.WaitForStatusAsync(id, "DeadLettered");
        dead.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("InvalidRecipient");
        dead.GetProperty("attempts").GetInt32().ShouldBe(1);
        (await host.GraphRequestsAsync()).Count.ShouldBe(1);

        var retry = await host.PostAsync(host.Admin, $"/api/v1/notifications/{id}/retry", new { });
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var sent = await host.WaitForStatusAsync(id, "Sent");
        sent.GetProperty("providerMessageId").GetString().ShouldStartWith("wamid.");
        (await host.GraphMessagesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_token_is_permanent_and_is_reported_with_its_own_kind()
    {
        await using var host = await EndToEndHost.StartAsync();
        using var client = host.Client(EndToEndHost.ExpiredTenantKey);

        var created = await host.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = EndToEndHost.Recipient,
            kind = "Template",
            eventKey = "e",
            referenceKey = "r",
            template = new { name = "booking_status_updated", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });
        var id = (await EndToEndHost.ReadAsync(created)).GetProperty("id").GetGuid();

        var row = await host.WaitForStatusAsync(id, "DeadLettered");

        row.GetProperty("error").GetString().ShouldNotBeNull().ShouldStartWith("TokenExpired");
        (await host.GraphRequestsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_disabled_tenant_is_skipped_not_failed()
    {
        await using var host = await EndToEndHost.StartAsync();
        using var client = host.Client(EndToEndHost.DisabledTenantKey);

        var created = await host.PostAsync(client, "/api/v1/events/booking-created", EndToEndHost.BookingEvent());
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var list = await Eventually.Async(
            async () => await EndToEndHost.ReadAsync(await host.Admin.GetAsync("/api/v1/notifications?status=Skipped", TestContext.Current.CancellationToken)),
            l => l.GetProperty("total").GetInt32() == 1);

        list.GetProperty("items")[0].GetProperty("tenantId").GetString().ShouldBe("tenant-disabled");
        (await host.GraphRequestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Readiness_checks_the_database_and_the_dispatcher()
    {
        await using var host = await EndToEndHost.StartAsync();

        await Eventually.Async(
            async () => (await host.Client("none").GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode,
            status => status == HttpStatusCode.OK);
    }
}

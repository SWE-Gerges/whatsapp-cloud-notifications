using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whatsapp.Tests.Support;
using Whatsapp.Webhooks;

namespace Whatsapp.Tests.Api;

/// <summary>The inbound webhook through the real API host: handshake, signature, tolerant parsing and redelivery.</summary>
public sealed class WebhookApiTests
{
    private const string Handshake = "/api/v1/webhooks/whatsapp";

    private static string HandshakeUrl(string mode, string token, string challenge) =>
        $"{Handshake}?hub.mode={Uri.EscapeDataString(mode)}&hub.verify_token={Uri.EscapeDataString(token)}&hub.challenge={Uri.EscapeDataString(challenge)}";

    // ---- handshake ----

    [Fact]
    public async Task The_handshake_echoes_the_challenge_as_plain_text_for_a_configured_verify_token()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await client.GetAsync(HandshakeUrl("subscribe", ApiFactory.TenantAVerifyToken, "1158201444"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("1158201444");
    }

    [Fact]
    public async Task The_verify_token_of_any_configured_tenant_is_accepted()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await client.GetAsync(HandshakeUrl("subscribe", ApiFactory.TenantBVerifyToken, "42"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("subscribe", "wrong-token", "42")]
    [InlineData("subscribe", "", "42")]
    [InlineData("unsubscribe", ApiFactory.TenantAVerifyToken, "42")]
    [InlineData("", ApiFactory.TenantAVerifyToken, "42")]
    [InlineData("SUBSCRIBE", ApiFactory.TenantAVerifyToken, "42")]
    [InlineData("subscribe", ApiFactory.TenantAVerifyToken, "")]
    public async Task A_wrong_token_mode_or_missing_challenge_is_forbidden(string mode, string token, string challenge)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await client.GetAsync(HandshakeUrl(mode, token, challenge), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task The_handshake_without_any_parameters_is_forbidden()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        (await client.GetAsync(Handshake, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task No_log_line_contains_a_verify_token_or_an_app_secret()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new ApiFactory(configureServices: services => services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
        using var client = factory.CreateClientWithKey(null);

        await client.GetAsync(HandshakeUrl("subscribe", ApiFactory.TenantAVerifyToken, "1"), TestContext.Current.CancellationToken);
        await client.GetAsync(HandshakeUrl("subscribe", "a-wrong-token-value", "1"), TestContext.Current.CancellationToken);
        await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"), secret: "some-wrong-secret");

        logs.Entries.ShouldNotBeEmpty();
        foreach (var entry in logs.Entries)
        {
            entry.Message.ShouldNotContain(ApiFactory.TenantAVerifyToken);
            entry.Message.ShouldNotContain("a-wrong-token-value");
            entry.Message.ShouldNotContain(ApiFactory.TenantAAppSecret);
            entry.Message.ShouldNotContain("some-wrong-secret");
        }
    }

    // ---- signature ----

    [Fact]
    public async Task A_correctly_signed_delivery_is_accepted_and_recorded()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var message = factory.InboundMessages().ShouldHaveSingleItem();
        message.TenantId.ShouldBe("tenant-a");
        message.MessageId.ShouldBe("wamid.INBOUND-TEXT-1");
        message.Sender.ShouldBe("+12025550100");
        message.Type.ShouldBe("text");
        message.ReceivedAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1772359200));
    }

    [Fact]
    public async Task A_tampered_body_is_unauthorized_and_changes_nothing()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        var original = Fixtures.Webhook("inbound-text.json");
        var signature = WebhookSignature.Sign(ApiFactory.TenantAAppSecret, Encoding.UTF8.GetBytes(original));
        var tampered = original.Replace("wamid.INBOUND-TEXT-1", "wamid.INJECTED", StringComparison.Ordinal);

        var response = await ApiFactory.PostWebhookAsync(client, tampered, signatureHeader: signature);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.InboundMessages().ShouldBeEmpty();
        factory.Windows().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_wrong_secret_is_unauthorized()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"), secret: "not-the-secret");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.InboundMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_signature_header_is_unauthorized()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"), secret: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.InboundMessages().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sha1=0123456789012345678901234567890123456789")]
    [InlineData("sha256=")]
    [InlineData("sha256=zzzz")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_signature_header_with_the_wrong_prefix_or_shape_is_unauthorized(string header)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"), signatureHeader: header);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.InboundMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_body_is_unauthorized_even_with_a_valid_signature_of_nothing()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var response = await ApiFactory.PostWebhookAsync(client, string.Empty);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_body_over_the_size_cap_is_rejected_with_413_before_it_is_verified()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        var padding = new string('x', 1024 * 1024);
        var body = Fixtures.Webhook("inbound-text.json").Replace("Hello, is my booking confirmed?", padding, StringComparison.Ordinal);

        var response = await ApiFactory.PostWebhookAsync(client, body);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        factory.InboundMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_just_under_the_cap_is_still_processed()
    {
        using var factory = new ApiFactory(new Dictionary<string, string?> { ["Whatsapp:Webhooks:MaxBodyBytes"] = "4096" });
        using var client = factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook("inbound-text.json");
        Encoding.UTF8.GetByteCount(body).ShouldBeLessThan(4096);

        (await ApiFactory.PostWebhookAsync(client, body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ApiFactory.PostWebhookAsync(client, body.Replace("Hello, is my booking confirmed?", new string('x', 5000), StringComparison.Ordinal)))
            .StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task The_tenant_comes_from_the_payload_never_from_a_header()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(ApiFactory.TenantBKey);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "tenant-b");

        // The payload names tenant A's phone number: it is verified with tenant A's secret and stored for tenant A.
        var asTenantB = await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"), secret: ApiFactory.TenantBAppSecret);
        var asTenantA = await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-text.json"), secret: ApiFactory.TenantAAppSecret);

        asTenantB.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        asTenantA.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.InboundMessages().ShouldHaveSingleItem().TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task A_delivery_for_the_other_tenant_is_stored_for_that_tenant()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook("inbound-text.json", ("100200300", ApiFactory.TenantBPhoneNumberId));

        (await ApiFactory.PostWebhookAsync(client, body, secret: ApiFactory.TenantBAppSecret)).StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.InboundMessages().ShouldHaveSingleItem().TenantId.ShouldBe("tenant-b");
    }

    // ---- unknown phone number ----

    [Fact]
    public async Task An_unknown_phone_number_id_is_acknowledged_with_200_ignored_and_counted()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        using var metrics = new MetricRecorder(factory.Services.GetRequiredService<IMeterFactory>());
        var body = Fixtures.Webhook("inbound-text.json", ("100200300", "999000111"));

        var response = await ApiFactory.PostWebhookAsync(client, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.InboundMessages().ShouldBeEmpty();
        factory.Windows().ShouldBeEmpty();
        metrics.Sum(WebhookMetrics.UnknownPhoneNumberIdsName).ShouldBe(1);
        metrics.Sum(WebhookMetrics.DeliveriesName, ("outcome", "ignored_unknown_phone_number")).ShouldBe(1);
    }

    // ---- tolerant parsing, end to end ----

    [Theory]
    [InlineData("inbound-image.json")]
    [InlineData("inbound-unknown-type.json")]
    [InlineData("status-sent.json")]
    [InlineData("status-failed.json")]
    [InlineData("status-unknown.json")]
    public async Task Every_fixture_is_accepted_whatever_it_contains(string fixture)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        (await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook(fixture))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_message_types_are_stored_by_type_and_unusable_items_are_dropped()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("inbound-unknown-type.json"));

        factory.InboundMessages().Select(m => (m.MessageId, m.Type)).ShouldBe(
        [
            ("wamid.INBOUND-HOLOGRAM-1", "hologram"),
            ("wamid.INBOUND-REACTION-1", "reaction"),
        ]);
    }

    [Fact]
    public async Task Receipts_of_every_kind_are_stored_including_statuses_this_service_does_not_know()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        await ApiFactory.PostWebhookAsync(client, Fixtures.Webhook("status-unknown.json"));

        factory.Receipts().Select(r => (r.ProviderMessageId, r.Status)).ShouldBe(
            [("wamid.OUTBOUND-3", "delivered"), ("wamid.OUTBOUND-2", "deleted"), ("wamid.OUTBOUND-4", "read")],
            ignoreOrder: true);
    }

    // ---- redelivery ----

    [Fact]
    public async Task The_same_inbound_delivery_posted_three_times_has_one_effect()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook("inbound-text.json");

        for (var i = 0; i < 3; i++)
        {
            (await ApiFactory.PostWebhookAsync(client, body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        factory.InboundMessages().ShouldHaveSingleItem();
        var window = factory.Windows().ShouldHaveSingleItem();
        window.LastInboundAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1772359200));
    }

    [Fact]
    public async Task The_same_status_delivery_posted_three_times_appends_one_receipt()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        var body = Fixtures.Webhook("status-delivered.json");

        for (var i = 0; i < 3; i++)
        {
            (await ApiFactory.PostWebhookAsync(client, body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var receipt = factory.Receipts().ShouldHaveSingleItem();
        receipt.ProviderMessageId.ShouldBe("wamid.OUTBOUND-1");
        receipt.Status.ShouldBe("delivered");
        receipt.TenantId.ShouldBe("tenant-a");
        receipt.NotificationId.ShouldBe(Guid.Parse("11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public async Task Parallel_redeliveries_of_one_body_are_still_one_effect()
    {
        using var factory = new ApiFactory();
        var body = Fixtures.Webhook("inbound-text.json");
        factory.CreateClientWithKey(null).Dispose();

        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            using var client = factory.CreateClientWithKey(null);
            return await ApiFactory.PostWebhookAsync(client, body);
        }, TestContext.Current.CancellationToken)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        factory.InboundMessages().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_webhook_routes_are_anonymous_and_documented_as_such()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        using var admin = factory.Admin();

        (await client.GetAsync(HandshakeUrl("subscribe", ApiFactory.TenantAVerifyToken, "1"), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var document = await admin.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        document.ShouldContain("/api/v1/webhooks/whatsapp");
    }
}

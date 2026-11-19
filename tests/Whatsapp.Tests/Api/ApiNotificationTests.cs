using System.Net;
using System.Text.Json;
using Whatsapp.Abstractions;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Api;

public sealed class ApiNotificationTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static object Template(string reference = "o-1", string recipient = TestData.Recipient, object? template = null) => new
    {
        recipient,
        kind = "Template",
        eventKey = "order-update",
        referenceKey = reference,
        template = template ?? new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
    };

    private static object FreeText(string reference = "f-1") => new
    {
        recipient = TestData.Recipient,
        kind = "FreeText",
        eventKey = "order-update",
        referenceKey = reference,
        text = "Your order is ready.",
    };

    // Authentication and authorisation.

    [Fact]
    public async Task Requests_without_a_key_are_rejected_with_a_problem_document()
    {
        using var client = _factory.CreateClientWithKey(null);

        var response = await client.GetAsync($"/api/v1/notifications/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await ApiFactory.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().ShouldBe(401);
        body.GetProperty("correlationId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("test-admin-key-0123456780")]
    [InlineData("TEST-TENANT-A-KEY-0123456")]
    public async Task Unknown_keys_are_rejected(string key)
    {
        using var client = _factory.CreateClientWithKey(key);

        var response = await client.GetAsync($"/api/v1/notifications/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_tenant_key_cannot_use_admin_routes()
    {
        using var client = _factory.TenantA();

        (await client.GetAsync("/api/v1/notifications", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApiFactory.PostAsync(client, $"/api/v1/notifications/{Guid.NewGuid()}/retry", new { })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ApiFactory.PostAsync(client, "/api/v1/admin/conversations/inbound", new { tenantId = "tenant-a", recipient = TestData.Recipient }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_admin_key_cannot_create_notifications_because_it_has_no_tenant()
    {
        using var client = _factory.Admin();

        (await ApiFactory.PostAsync(client, "/api/v1/notifications", Template())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // Enqueue.

    [Fact]
    public async Task A_new_notification_is_accepted_with_a_location_and_queued_for_the_key_tenant()
    {
        using var client = _factory.TenantA();

        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", Template());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await ApiFactory.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("Pending");
        body.GetProperty("duplicate").GetBoolean().ShouldBeFalse();
        var id = body.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/notifications/{id}");

        var row = _factory.Rows().ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.TenantId.ShouldBe("tenant-a");
        row.Recipient.ShouldBe(TestData.Recipient);
    }

    [Fact]
    public async Task The_same_key_again_returns_200_with_duplicate_true_and_the_original_id()
    {
        using var client = _factory.TenantA();
        var first = await ApiFactory.ReadJsonAsync(await ApiFactory.PostAsync(client, "/api/v1/notifications", Template()));

        var second = await ApiFactory.PostAsync(client, "/api/v1/notifications", Template());

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ApiFactory.ReadJsonAsync(second);
        body.GetProperty("duplicate").GetBoolean().ShouldBeTrue();
        body.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
        _factory.Rows().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Two_tenants_may_use_the_same_keys_independently()
    {
        var a = await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template());
        var b = await ApiFactory.PostAsync(_factory.TenantB(), "/api/v1/notifications", Template());

        a.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        b.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Rows().Select(r => r.TenantId).ShouldBe(["tenant-a", "tenant-b"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_national_number_is_normalised_with_the_default_region()
    {
        using var client = _factory.TenantA();

        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = "(202) 555-0100",
            defaultRegion = "US",
            kind = "FreeText",
            eventKey = "e",
            referenceKey = "r",
            text = "hi",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Rows().ShouldHaveSingleItem().Recipient.ShouldBe("+12025550100");
    }

    [Fact]
    public async Task Free_text_variants_are_rendered_in_the_preferred_language_with_placeholders()
    {
        using var client = _factory.TenantA();

        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "FreeText",
            eventKey = "e",
            referenceKey = "r",
            textVariants = new Dictionary<string, string> { ["en"] = "Hello {name}", ["ar"] = "مرحبا {name}" },
            placeholders = new Dictionary<string, string> { ["name"] = "Sam" },
            preferredLanguage = "ar",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Rows().ShouldHaveSingleItem().Text.ShouldBe("مرحبا Sam");
    }

    [Fact]
    public async Task Free_text_may_carry_a_fallback_template()
    {
        using var client = _factory.TenantA();

        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "FreeText",
            eventKey = "e",
            referenceKey = "r",
            text = "Hello",
            template = new { name = "booking_status_updated", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var row = _factory.Rows().ShouldHaveSingleItem();
        row.Kind.ShouldBe(NotificationKind.FreeText);
        row.TemplateName.ShouldBe("booking_status_updated");
        row.TemplateLanguage.ShouldBeNull();
    }

    public static TheoryData<string, object, string> InvalidRequests => new()
    {
        { "invalid recipient", new { recipient = "12", kind = "FreeText", eventKey = "e", referenceKey = "r", text = "x" }, "recipient" },
        { "national number without region", new { recipient = "2025550100", kind = "FreeText", eventKey = "e", referenceKey = "r", text = "x" }, "recipient" },
        { "blank event key", new { recipient = TestData.Recipient, kind = "FreeText", eventKey = " ", referenceKey = "r", text = "x" }, "eventKey" },
        { "long event key", new { recipient = TestData.Recipient, kind = "FreeText", eventKey = new string('e', 101), referenceKey = "r", text = "x" }, "eventKey" },
        { "blank reference key", new { recipient = TestData.Recipient, kind = "FreeText", eventKey = "e", referenceKey = " ", text = "x" }, "referenceKey" },
        { "template kind without template", new { recipient = TestData.Recipient, kind = "Template", eventKey = "e", referenceKey = "r" }, "template" },
        { "template kind with text", new { recipient = TestData.Recipient, kind = "Template", eventKey = "e", referenceKey = "r", text = "x", template = new { name = "booking_status_updated", parameters = new[] { "a", "b", "c" } } }, "text" },
        { "unknown template", new { recipient = TestData.Recipient, kind = "Template", eventKey = "e", referenceKey = "r", template = new { name = "nope", parameters = Array.Empty<string>() } }, "template.name" },
        { "wrong parameter count", new { recipient = TestData.Recipient, kind = "Template", eventKey = "e", referenceKey = "r", template = new { name = "booking_status_updated", parameters = new[] { "a" } } }, "template" },
        { "parameter with a new line", new { recipient = TestData.Recipient, kind = "Template", eventKey = "e", referenceKey = "r", template = new { name = "booking_status_updated", parameters = new[] { "a\nb", "b", "c" } } }, "template" },
        { "unsupported template language", new { recipient = TestData.Recipient, kind = "Template", eventKey = "e", referenceKey = "r", template = new { name = "booking_status_updated", languageCode = "fr", parameters = new[] { "a", "b", "c" } } }, "template" },
        { "free text without text", new { recipient = TestData.Recipient, kind = "FreeText", eventKey = "e", referenceKey = "r" }, "text" },
        { "text and variants together", new { recipient = TestData.Recipient, kind = "FreeText", eventKey = "e", referenceKey = "r", text = "x", textVariants = new Dictionary<string, string> { ["en"] = "y" } }, "text" },
        { "missing placeholder value", new { recipient = TestData.Recipient, kind = "FreeText", eventKey = "e", referenceKey = "r", text = "Hi {name}", placeholders = new Dictionary<string, string> { ["other"] = "x" } }, "placeholders" },
        { "text too long", new { recipient = TestData.Recipient, kind = "FreeText", eventKey = "e", referenceKey = "r", text = new string('x', 5000) }, "text" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_return_a_validation_problem_naming_the_field(string reason, object request, string field)
    {
        using var client = _factory.TenantA();

        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, reason);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await ApiFactory.ReadJsonAsync(response);
        body.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body.ToString());
        _factory.Rows().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"recipient":"+12025550100"}""")]
    [InlineData("""{"recipient":"+12025550100","kind":"Carrier","eventKey":"e","referenceKey":"r"}""")]
    [InlineData("not json")]
    public async Task Malformed_bodies_return_a_400_problem_document(string body)
    {
        using var client = _factory.TenantA();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/notifications", content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    // Read, list, retry.

    [Fact]
    public async Task A_notification_can_be_read_by_its_tenant_but_not_by_another_tenant()
    {
        var created = await ApiFactory.ReadJsonAsync(await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template()));
        var id = created.GetProperty("id").GetGuid();

        var own = await _factory.TenantA().GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken);
        var other = await _factory.TenantB().GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken);
        var admin = await _factory.Admin().GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken);

        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ApiFactory.ReadJsonAsync(own);
        body.GetProperty("status").GetString().ShouldBe("Pending");
        body.GetProperty("tenantId").GetString().ShouldBe("tenant-a");
        body.GetProperty("templateName").GetString().ShouldBe("booking_status_updated");
        body.GetProperty("templateParameters").GetArrayLength().ShouldBe(3);
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        admin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_problem_and_a_malformed_id_is_not_routed()
    {
        using var client = _factory.TenantA();

        var missing = await client.GetAsync($"/api/v1/notifications/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        (await client.GetAsync("/api/v1/notifications/not-a-guid", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_admin_list_shows_all_tenants_with_filters_and_paging()
    {
        await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template("o-1"));
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template("o-2", TestData.OtherRecipient));
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await ApiFactory.PostAsync(_factory.TenantB(), "/api/v1/notifications", Template("o-3"));
        await _factory.ProcessOutboxAsync();
        using var admin = _factory.Admin();

        var all = await ApiFactory.ReadJsonAsync(await admin.GetAsync("/api/v1/notifications", TestContext.Current.CancellationToken));
        all.GetProperty("total").GetInt32().ShouldBe(3);
        all.GetProperty("items")[0].GetProperty("referenceKey").GetString().ShouldBe("o-3");

        var byRecipient = await ApiFactory.ReadJsonAsync(await admin.GetAsync(
            "/api/v1/notifications?recipient=%2B12025550143", TestContext.Current.CancellationToken));
        byRecipient.GetProperty("total").GetInt32().ShouldBe(1);

        var byTenant = await ApiFactory.ReadJsonAsync(await admin.GetAsync(
            "/api/v1/notifications?tenantId=tenant-b", TestContext.Current.CancellationToken));
        byTenant.GetProperty("total").GetInt32().ShouldBe(1);

        var paged = await ApiFactory.ReadJsonAsync(await admin.GetAsync(
            "/api/v1/notifications?page=2&pageSize=2", TestContext.Current.CancellationToken));
        paged.GetProperty("items").GetArrayLength().ShouldBe(1);
        paged.GetProperty("page").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task The_admin_list_filters_by_status()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template("o-1"));
        await _factory.ProcessOutboxAsync();
        await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template("o-2"));
        using var admin = _factory.Admin();

        var sent = await ApiFactory.ReadJsonAsync(await admin.GetAsync("/api/v1/notifications?status=Sent", TestContext.Current.CancellationToken));
        var pending = await ApiFactory.ReadJsonAsync(await admin.GetAsync("/api/v1/notifications?status=Pending", TestContext.Current.CancellationToken));

        sent.GetProperty("total").GetInt32().ShouldBe(1);
        pending.GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("status=Nope")]
    public async Task Invalid_list_parameters_are_rejected(string query)
    {
        using var admin = _factory.Admin();

        (await admin.GetAsync($"/api/v1/notifications?{query}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_dead_lettered_notification_can_be_retried_and_is_then_delivered()
    {
        _factory.Graph.Respond(HttpStatusCode.BadRequest, TestData.ErrorBody(131026, "Message undeliverable"));
        var created = await ApiFactory.ReadJsonAsync(await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template()));
        var id = created.GetProperty("id").GetGuid();
        await _factory.ProcessOutboxAsync();
        using var admin = _factory.Admin();

        var dead = await ApiFactory.ReadJsonAsync(await admin.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken));
        dead.GetProperty("status").GetString().ShouldBe("DeadLettered");
        dead.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("InvalidRecipient");

        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var retry = await ApiFactory.PostAsync(admin, $"/api/v1/notifications/{id}/retry", new { });
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.ProcessOutboxAsync();

        var sent = await ApiFactory.ReadJsonAsync(await admin.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken));
        sent.GetProperty("status").GetString().ShouldBe("Sent");
        sent.GetProperty("providerMessageId").GetString().ShouldBe("wamid.TEST-1");
    }

    [Fact]
    public async Task Retrying_a_notification_that_is_not_failed_is_a_409_and_an_unknown_id_a_404()
    {
        var created = await ApiFactory.ReadJsonAsync(await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", Template()));
        var id = created.GetProperty("id").GetGuid();
        using var admin = _factory.Admin();

        var pending = await ApiFactory.PostAsync(admin, $"/api/v1/notifications/{id}/retry", new { });
        var missing = await ApiFactory.PostAsync(admin, $"/api/v1/notifications/{Guid.NewGuid()}/retry", new { });

        pending.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        pending.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Delivery through the HTTP surface.

    [Fact]
    public async Task A_queued_template_notification_is_delivered_with_the_tenant_credentials()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        await ApiFactory.PostAsync(_factory.TenantB(), "/api/v1/notifications", Template());

        await _factory.ProcessOutboxAsync();

        var request = _factory.Graph.Requests.ShouldHaveSingleItem();
        request.Uri.ToString().ShouldBe("https://graph.example.com/v1/400500600/messages");
        request.Authorization.ShouldBe("Bearer token-b-for-tests-only");
        request.Body.ShouldContain("\"name\":\"booking_status_updated\"");
    }

    [Fact]
    public async Task Free_text_is_skipped_until_an_inbound_message_opens_the_window()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        using var tenant = _factory.TenantA();
        await ApiFactory.PostAsync(tenant, "/api/v1/notifications", FreeText("f-1"));
        await _factory.ProcessOutboxAsync();
        _factory.Graph.Requests.ShouldBeEmpty();
        _factory.Rows().Single().Status.ShouldBe(NotificationStatus.Skipped);

        var inbound = await ApiFactory.PostAsync(_factory.Admin(), "/api/v1/admin/conversations/inbound", new { tenantId = "tenant-a", recipient = TestData.Recipient });
        inbound.StatusCode.ShouldBe(HttpStatusCode.OK);
        var window = await ApiFactory.ReadJsonAsync(inbound);
        window.GetProperty("windowOpenUntil").GetDateTimeOffset().ShouldBe(_factory.Time.GetUtcNow().AddHours(24));

        await ApiFactory.PostAsync(tenant, "/api/v1/notifications", FreeText("f-2"));
        await _factory.ProcessOutboxAsync();

        _factory.Graph.Requests.ShouldHaveSingleItem().Body.ShouldContain("\"type\":\"text\"");
    }

    [Theory]
    [InlineData("""{"tenantId":"nobody","recipient":"+12025550100"}""", "tenantId")]
    [InlineData("""{"tenantId":"tenant-a","recipient":"12"}""", "recipient")]
    [InlineData("""{"tenantId":"tenant-a","recipient":"+12025550100","receivedAt":"2030-01-01T00:00:00Z"}""", "receivedAt")]
    public async Task Invalid_inbound_simulations_are_rejected(string body, string field)
    {
        using var admin = _factory.Admin();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await admin.PostAsync("/api/v1/admin/conversations/inbound", content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ApiFactory.ReadJsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task An_inbound_message_in_the_past_opens_a_correspondingly_shorter_window()
    {
        using var admin = _factory.Admin();

        var response = await ApiFactory.PostAsync(admin, "/api/v1/admin/conversations/inbound", new
        {
            tenantId = "tenant-a",
            recipient = TestData.Recipient,
            receivedAt = _factory.Time.GetUtcNow().AddHours(-23),
        });

        var window = await ApiFactory.ReadJsonAsync(response);
        window.GetProperty("windowOpenUntil").GetDateTimeOffset().ShouldBe(_factory.Time.GetUtcNow().AddHours(1));
    }

    [Fact]
    public async Task The_response_json_uses_camel_case_names_and_string_enums_and_omits_nulls()
    {
        var created = await ApiFactory.ReadJsonAsync(await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/notifications", FreeText()));
        var id = created.GetProperty("id").GetGuid();

        var body = await ApiFactory.ReadJsonAsync(await _factory.TenantA().GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken));

        body.GetProperty("kind").ValueKind.ShouldBe(JsonValueKind.String);
        body.TryGetProperty("providerMessageId", out _).ShouldBeFalse();
        body.TryGetProperty("ProviderMessageId", out _).ShouldBeFalse();
    }
}

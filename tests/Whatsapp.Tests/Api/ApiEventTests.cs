using System.Net;
using Whatsapp.Abstractions;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Api;

public sealed class ApiEventTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static Dictionary<string, object?> Booking(
        string bookingId = "bk-1001",
        string status = "Confirmed",
        bool optedIn = true,
        string callingCode = "1",
        string national = "2025550100") => new()
    {
        ["bookingId"] = bookingId,
        ["reference"] = "REF-1001",
        ["customerFirstName"] = "Sam",
        ["phoneCountryCallingCode"] = callingCode,
        ["phoneNationalNumber"] = national,
        ["bookingType"] = "Hotel",
        ["status"] = status,
        ["details"] = new { checkInDate = "2026-03-14" },
        ["companyName"] = "Acme Travel",
        ["createdAt"] = "2026-03-01T09:30:00Z",
        ["preferredLanguage"] = "en",
        ["whatsappUpdatesEnabled"] = optedIn,
    };

    [Fact]
    public async Task Booking_created_is_accepted_and_queued_for_the_tenant_of_the_key()
    {
        var response = await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/events/booking-created", Booking());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await ApiFactory.ReadJsonAsync(response);
        body.GetProperty("eventKey").GetString().ShouldBe("booking-created");
        body.GetProperty("referenceKey").GetString().ShouldBe("bk-1001");

        var row = _factory.Rows().ShouldHaveSingleItem();
        row.TenantId.ShouldBe("tenant-a");
        row.TemplateName.ShouldBe("booking_created");
        row.Recipient.ShouldBe("+12025550100");
        row.Status.ShouldBe(NotificationStatus.Pending);
        _factory.Graph.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Posting_the_same_event_twice_queues_and_sends_it_once()
    {
        _factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        using var client = _factory.TenantA();

        (await ApiFactory.PostAsync(client, "/api/v1/events/booking-created", Booking())).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ApiFactory.PostAsync(client, "/api/v1/events/booking-created", Booking())).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.ProcessOutboxAsync();
        await _factory.ProcessOutboxAsync();

        _factory.Rows().ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.Sent);
        _factory.Graph.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Status_changes_are_queued_per_status_and_use_the_status_label()
    {
        using var client = _factory.TenantA();

        await ApiFactory.PostAsync(client, "/api/v1/events/booking-status-changed", Booking(status: "ConfirmedPendingPayment"));
        await ApiFactory.PostAsync(client, "/api/v1/events/booking-status-changed", Booking(status: "Cancelled"));

        var rows = _factory.Rows();
        rows.Count.ShouldBe(2);
        rows.Select(r => r.TemplateParameters[2]).ShouldBe(["Confirmed - Pending Payment", "Cancelled"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_status_change_for_a_customer_who_did_not_opt_in_is_accepted_but_queues_nothing()
    {
        var response = await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/events/booking-status-changed", Booking(optedIn: false));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Rows().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tenant_id_in_the_body_cannot_override_the_key_tenant()
    {
        var body = Booking();
        body["tenantId"] = "tenant-b";

        await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/events/booking-created", body);

        _factory.Rows().ShouldHaveSingleItem().TenantId.ShouldBe("tenant-a");
    }

    public static TheoryData<string, string, string> InvalidBookings => new()
    {
        { "bad phone", "phoneNationalNumber", "123" },
        { "unassigned calling code", "phoneCountryCallingCode", "999" },
        { "blank booking id", "bookingId", " " },
        { "blank reference", "reference", "" },
        { "blank name", "customerFirstName", " " },
        { "blank company", "companyName", " " },
    };

    [Theory]
    [MemberData(nameof(InvalidBookings))]
    public async Task Invalid_events_are_rejected_with_the_offending_field(string reason, string field, string value)
    {
        var body = Booking();
        body[field] = value;

        var response = await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/events/booking-created", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, reason);
        var problem = await ApiFactory.ReadJsonAsync(response);
        var expected = field == "phoneCountryCallingCode" ? "phoneNationalNumber" : field;
        problem.GetProperty("errors").TryGetProperty(expected, out _).ShouldBeTrue(problem.ToString());
        _factory.Rows().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("bookingType", "Cruise")]
    [InlineData("status", "Exploded")]
    public async Task Unknown_enum_names_are_a_400(string field, string value)
    {
        var body = Booking();
        body[field] = value;

        var response = await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/events/booking-created", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_missing_required_field_is_a_400()
    {
        var body = Booking();
        body.Remove("createdAt");

        var response = await ApiFactory.PostAsync(_factory.TenantA(), "/api/v1/events/booking-status-changed", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Events_require_a_tenant_key()
    {
        (await ApiFactory.PostAsync(_factory.CreateClientWithKey(null), "/api/v1/events/booking-created", Booking())).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ApiFactory.PostAsync(_factory.Admin(), "/api/v1/events/booking-created", Booking())).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_cloud_api_outage_never_fails_the_event_request()
    {
        // No inline retries: the fake clock would never advance through a client side backoff delay.
        using var factory = new ApiFactory(new Dictionary<string, string?> { ["Whatsapp:Client:MaxRetryAttempts"] = "0" });
        factory.Graph.Respond(HttpStatusCode.ServiceUnavailable, "<html>down</html>");

        var response = await ApiFactory.PostAsync(factory.TenantA(), "/api/v1/events/booking-created", Booking());
        await factory.ProcessOutboxAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Graph.Requests.ShouldHaveSingleItem();
        var row = factory.Rows().ShouldHaveSingleItem();
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.Attempts.ShouldBe(1);
        row.NextAttemptAt.ShouldBeGreaterThan(factory.Time.GetUtcNow());
    }
}

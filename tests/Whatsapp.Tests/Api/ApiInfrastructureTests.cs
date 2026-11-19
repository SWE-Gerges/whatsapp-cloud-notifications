using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Api;

public sealed class ApiInfrastructureTests
{
    [Fact]
    public async Task Liveness_and_readiness_are_anonymous_and_healthy()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_when_the_database_is_unreachable()
    {
        using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = "Data Source=/this/path/does/not/exist/db.sqlite",
            ["Whatsapp:Outbox:SchemaMode"] = "None",
        });
        using var client = factory.CreateClientWithKey(null);

        var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_correlation_id_from_the_caller_is_echoed()
    {
        using var factory = new ApiFactory();
        using var client = factory.TenantA();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/notifications/{Guid.NewGuid()}");
        request.Headers.Add("X-Correlation-Id", "trace-abc.123:xyz");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["trace-abc.123:xyz"]);
        (await ApiFactory.ReadJsonAsync(response)).GetProperty("correlationId").GetString().ShouldBe("trace-abc.123:xyz");
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("semi;colon")]
    [InlineData("a-very-long-id-a-very-long-id-a-very-long-id-a-very-long-id-a-very-long-id")]
    public async Task An_unsafe_correlation_id_is_replaced_by_a_generated_one(string supplied)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", supplied);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var echoed = response.Headers.GetValues("X-Correlation-Id").Single();
        echoed.ShouldNotBe(supplied);
        echoed.Length.ShouldBe(32);
    }

    [Fact]
    public async Task A_correlation_id_is_generated_when_the_caller_sends_none()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        var first = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var second = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        first.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBe(second.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Unknown_routes_answer_with_a_problem_document()
    {
        using var factory = new ApiFactory();
        using var client = factory.TenantA();

        var response = await client.GetAsync("/api/v1/nothing-here", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Unhandled_errors_are_a_generic_500_problem_without_stack_trace_or_message()
    {
        var enqueuer = ThrowingEnqueuer(new InvalidOperationException("secret connection detail"));
        using var factory = new ApiFactory(configureServices: services => services.AddSingleton(enqueuer));
        using var client = factory.TenantA();

        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "FreeText",
            eventKey = "e",
            referenceKey = "r",
            text = "hi",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain("secret connection detail");
        text.ShouldNotContain("InvalidOperationException");
        text.ShouldNotContain(" at ");
        text.ShouldContain("correlationId");
    }

    [Fact]
    public async Task A_domain_validation_error_from_the_enqueuer_is_a_400_problem()
    {
        var enqueuer = ThrowingEnqueuer(new NotificationValidationException("Text must not exceed 4096 characters."));
        using var factory = new ApiFactory(configureServices: services => services.AddSingleton(enqueuer));
        using var client = factory.TenantA();

        var response = await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "FreeText",
            eventKey = "e",
            referenceKey = "r",
            text = "hi",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Text must not exceed 4096 characters.");
    }

    [Theory]
    [InlineData("Api:AdminKey", "short")]
    [InlineData("Api:TenantKeys:0:Key", "short")]
    [InlineData("Api:TenantKeys:1:Key", ApiFactory.TenantAKey)]
    [InlineData("Api:TenantKeys:1:TenantId", "tenant-without-whatsapp-settings")]
    [InlineData("Database:ConnectionString", "")]
    [InlineData("Whatsapp:Tenants:tenant-a:BaseUrl", "not a url")]
    [InlineData("Whatsapp:Outbox:BatchSize", "0")]
    [InlineData("Whatsapp:ConversationWindow:Duration", "24:00:00")]
    public void Invalid_configuration_stops_the_host_at_startup(string key, string value)
    {
        using var factory = new ApiFactory(new Dictionary<string, string?> { [key] = value });

        Should.Throw<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task The_background_dispatcher_delivers_queued_notifications_on_its_own()
    {
        using var factory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["Whatsapp:Outbox:DispatcherEnabled"] = "true",
                ["Whatsapp:Outbox:PollInterval"] = "00:00:00.050",
            },
            configureServices: services => services.AddSingleton<TimeProvider>(TimeProvider.System));
        factory.Graph.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        using var client = factory.TenantA();

        var created = await ApiFactory.ReadJsonAsync(await ApiFactory.PostAsync(client, "/api/v1/notifications", new
        {
            recipient = TestData.Recipient,
            kind = "Template",
            eventKey = "e",
            referenceKey = "r",
            template = new { name = "booking_status_updated", languageCode = "en", parameters = new[] { "Sam", "REF-1", "Confirmed" } },
        }));
        var id = created.GetProperty("id").GetGuid();

        var status = await WaitForStatusAsync(client, id, "Sent");

        status.ShouldBe("Sent");
        factory.Graph.Requests.ShouldHaveSingleItem();
    }

    private static INotificationEnqueuer ThrowingEnqueuer(Exception exception)
    {
        var enqueuer = Substitute.For<INotificationEnqueuer>();
        enqueuer.EnqueueAsync(default!, default).ReturnsForAnyArgs(_ => Task.FromException<EnqueueResult>(exception));
        return enqueuer;
    }

    private static async Task<string?> WaitForStatusAsync(HttpClient client, Guid id, string expected)
    {
        string? status = null;
        for (var attempt = 0; attempt < 200 && status != expected; attempt++)
        {
            var body = await ApiFactory.ReadJsonAsync(await client.GetAsync($"/api/v1/notifications/{id}", TestContext.Current.CancellationToken));
            status = body.GetProperty("status").GetString();
            if (status != expected)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        return status;
    }
}

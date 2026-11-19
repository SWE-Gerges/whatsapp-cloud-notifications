using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whatsapp.Abstractions;
using Whatsapp.CloudApi;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.CloudApi;

public class WhatsappCloudClientTests
{
    private static readonly WhatsappTemplateMessage Template =
        new(TestData.Recipient, "booking_created", "en", ["Sam", "Acme Travel"]);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly StubHttpMessageHandler _handler;

    public WhatsappCloudClientTests()
    {
        _handler = new StubHttpMessageHandler(_time);
    }

    private (WhatsappCloudClient Client, HttpClient Http) CreateClient(
        WhatsappClientOptions? options = null,
        ILogger<WhatsappCloudClient>? logger = null)
    {
        var http = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new WhatsappCloudClient(
            http,
            Options.Create(options ?? new WhatsappClientOptions()),
            _time,
            logger ?? NullLogger<WhatsappCloudClient>.Instance);
        return (client, http);
    }

    [Fact]
    public async Task Posts_to_the_tenant_endpoint_with_bearer_token_and_returns_the_message_id()
    {
        _handler.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();

        var result = await client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);

        result.MessageId.ShouldBe("wamid.TEST-1");
        result.ContactWaId.ShouldBe("12025550100");
        var request = _handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ToString().ShouldBe("https://graph.example.com/v1/100200300/messages");
        request.Authorization.ShouldBe($"Bearer {TestData.Token}");
        request.MediaType.ShouldBe("application/json");
        request.Body.ShouldContain("\"type\":\"template\"");
    }

    [Fact]
    public async Task Does_not_store_tenant_state_on_the_shared_http_client()
    {
        _handler.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, http) = CreateClient();

        await client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);

        http.DefaultRequestHeaders.Authorization.ShouldBeNull();
        http.BaseAddress.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_calls_for_different_tenants_never_mix_credentials()
    {
        _handler.Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();
        var tenantA = TestData.Tenant("tenant-a", token: "token-a");
        var tenantB = TestData.Tenant("tenant-b", token: "token-b") with { PhoneNumberId = "999888777" };

        var calls = Enumerable.Range(0, 40)
            .Select(i => client.SendAsync(Template, i % 2 == 0 ? tenantA : tenantB, TestContext.Current.CancellationToken));
        await Task.WhenAll(calls);

        foreach (var request in _handler.Requests)
        {
            var isA = request.Uri.AbsolutePath.Contains("100200300", StringComparison.Ordinal);
            request.Authorization.ShouldBe(isA ? "Bearer token-a" : "Bearer token-b");
        }
    }

    [Fact]
    public async Task Transient_failure_is_retried_until_it_succeeds()
    {
        _handler.Respond(HttpStatusCode.ServiceUnavailable, "<html>down</html>")
            .Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();

        var task = client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);
        var result = await _time.RunWithTimeAsync(task);

        result.MessageId.ShouldBe("wamid.TEST-1");
        _handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Retry_after_header_is_honoured_before_the_next_attempt()
    {
        _handler.Respond(
                HttpStatusCode.TooManyRequests,
                TestData.ErrorBody(130429, "Rate limit hit"),
                r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3)))
            .Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();

        var task = client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);
        await _time.RunWithTimeAsync(task, TimeSpan.FromMilliseconds(100));

        var requests = _handler.Requests;
        requests.Count.ShouldBe(2);
        (requests[1].At - requests[0].At).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3));
        (requests[1].At - requests[0].At).ShouldBeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Retry_after_beyond_the_inline_limit_is_not_waited_for_and_is_reported_on_the_exception()
    {
        _handler.Respond(
            HttpStatusCode.TooManyRequests,
            TestData.ErrorBody(130429, "Rate limit hit"),
            r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)));
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.RateLimited);
        ex.IsTransient.ShouldBeTrue();
        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(120));
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Permanent_failure_is_never_retried()
    {
        _handler.Respond(HttpStatusCode.BadRequest, TestData.ErrorBody(131026, "Message undeliverable"));
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.InvalidRecipient);
        ex.IsTransient.ShouldBeFalse();
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.GraphError.ShouldNotBeNull().Code.ShouldBe(131026);
        ex.GraphError.FbTraceId.ShouldBe("trace-123");
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_token_is_permanent_with_its_own_kind()
    {
        _handler.Respond(HttpStatusCode.Unauthorized, TestData.ErrorBody(190, "Session has expired", subcode: 463));
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.TokenExpired);
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Retries_are_bounded_and_the_last_error_is_surfaced()
    {
        _handler.Respond(HttpStatusCode.InternalServerError, TestData.ErrorBody(2, "Service temporarily unavailable"));
        var (client, _) = CreateClient(new WhatsappClientOptions { MaxRetryAttempts = 2 });

        var task = client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<WhatsappApiException>(() => _time.RunWithTimeAsync(task));

        ex.Kind.ShouldBe(WhatsappErrorKind.ServerError);
        _handler.Requests.Count.ShouldBe(3);
    }

    private static HttpRequestException Failure(HttpRequestError error) => new(error, "connection failure");

    // A failure that provably happened before the request left is the only kind of network error that is retried here.
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    [InlineData(HttpRequestError.VersionNegotiationError)]
    public async Task A_failure_that_provably_never_left_is_transient_and_retried(HttpRequestError error)
    {
        _handler.Throw(Failure(error)).Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();

        var task = client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);
        var result = await _time.RunWithTimeAsync(task);

        result.MessageId.ShouldBe("wamid.TEST-1");
        _handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Persistent_connect_errors_surface_as_a_typed_transient_exception_with_a_known_outcome()
    {
        _handler.Throw(Failure(HttpRequestError.ConnectionError));
        var (client, _) = CreateClient(new WhatsappClientOptions { MaxRetryAttempts = 0 });

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.Network);
        ex.IsTransient.ShouldBeTrue();
        ex.OutcomeUnknown.ShouldBeFalse();
        ex.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    // Once the request may have been written, a retry could deliver the message twice, so it is surfaced instead.
    [Theory]
    [InlineData(HttpRequestError.Unknown)]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.HttpProtocolError)]
    public async Task A_failure_after_the_request_may_have_been_written_is_not_retried_and_has_an_unknown_outcome(HttpRequestError error)
    {
        _handler.Throw(Failure(error)).Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.Network);
        ex.OutcomeUnknown.ShouldBeTrue();
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_broken_stream_is_an_unknown_outcome_and_is_not_retried()
    {
        _handler.Throw(new IOException("The response ended prematurely.")).Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.OutcomeUnknown.ShouldBeTrue();
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_hanging_request_times_out_with_an_unknown_outcome_and_is_not_retried()
    {
        _handler.Hang().Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient(new WhatsappClientOptions { RequestTimeout = TimeSpan.FromSeconds(10) });

        var task = client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => _time.RunWithTimeAsync(task, TimeSpan.FromSeconds(1)));

        ex.Kind.ShouldBe(WhatsappErrorKind.Timeout);
        ex.IsTransient.ShouldBeTrue();
        ex.OutcomeUnknown.ShouldBeTrue();
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_call_the_transport_cancelled_itself_is_an_aborted_connection_not_a_timeout()
    {
        _handler.Throw(new OperationCanceledException("The request was aborted.")).Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.Network);
        ex.OutcomeUnknown.ShouldBeTrue();
        ex.Message.ShouldContain("aborted");
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_success_answer_without_a_message_id_is_an_unknown_outcome()
    {
        _handler.Respond(HttpStatusCode.OK, """{"messaging_product":"whatsapp"}""");
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.InvalidResponse);
        ex.OutcomeUnknown.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 2)]
    [InlineData(HttpStatusCode.BadRequest, 131026)]
    [InlineData(HttpStatusCode.TooManyRequests, 130429)]
    public async Task An_error_response_is_never_an_unknown_outcome(HttpStatusCode status, int code)
    {
        _handler.Respond(status, TestData.ErrorBody(code));
        var (client, _) = CreateClient(new WhatsappClientOptions { MaxRetryAttempts = 0 });

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.OutcomeUnknown.ShouldBeFalse();
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped_or_retried()
    {
        _handler.Hang();
        var (client, _) = CreateClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var task = client.SendAsync(Template, TestData.Tenant(), cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => task);
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Success_without_a_message_id_is_not_retried()
    {
        _handler.Respond(HttpStatusCode.OK, "{}");
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.InvalidResponse);
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_token_fails_before_any_request_is_made()
    {
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(token: ""), TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(WhatsappErrorKind.Authentication);
        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Phone_numbers_in_provider_messages_are_masked_on_the_exception()
    {
        _handler.Respond(
            HttpStatusCode.BadRequest,
            TestData.ErrorBody(131026, "Message to +12025550100 is undeliverable"));
        var (client, _) = CreateClient();

        var ex = await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));

        ex.Message.ShouldNotContain("12025550100");
        ex.Message.ShouldContain("+1202****0100");
    }

    [Fact]
    public async Task Logs_never_contain_the_token_or_the_full_recipient()
    {
        var logger = new CapturingLogger<WhatsappCloudClient>();
        _handler.Respond(HttpStatusCode.BadRequest, TestData.ErrorBody(131026, "Undeliverable +12025550100"))
            .Respond(HttpStatusCode.OK, TestData.SuccessBody);
        var (client, _) = CreateClient(logger: logger);

        await Should.ThrowAsync<WhatsappApiException>(
            () => client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken));
        await client.SendAsync(Template, TestData.Tenant(), TestContext.Current.CancellationToken);

        logger.Messages.ShouldNotBeEmpty();
        logger.Messages.ShouldAllBe(m => !m.Contains(TestData.Token, StringComparison.Ordinal));
        logger.Messages.ShouldAllBe(m => !m.Contains("12025550100", StringComparison.Ordinal));
    }
}

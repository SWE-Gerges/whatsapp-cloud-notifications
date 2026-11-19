using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Whatsapp.FakeGraph;
using Whatsapp.IntegrationTests.Support;
using Whatsapp.Webhooks;

namespace Whatsapp.IntegrationTests;

/// <summary>
/// The fake Graph server on its own: the signed webhooks it posts after accepting a message, the inbound simulation,
/// per-recipient outcomes and the accept-then-drop fault. The webhook signatures are checked with the verifier of the
/// service, so a fake that signed differently would be caught here.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FakeGraphTests
{
    private const string AppSecret = "fake-graph-test-secret-only";
    private const string CallbackUrl = "http://service.test/api/v1/webhooks/whatsapp";
    private const string Recipient = "+12025550100";

    private sealed record Posted(Uri Uri, string? Signature, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        public JsonElement Value => Json.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value");

        public JsonElement Status => Value.GetProperty("statuses")[0];
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<Posted> _posted = [];

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

        public List<Posted> Posted
        {
            get
            {
                lock (_gate)
                {
                    return [.. _posted];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var signature = request.Headers.TryGetValues("X-Hub-Signature-256", out var values) ? values.Single() : null;
            lock (_gate)
            {
                _posted.Add(new Posted(request.RequestUri!, signature, body));
            }

            return new HttpResponseMessage(Answer);
        }
    }

    private sealed class Fake : IDisposable
    {
        private readonly WebApplicationFactory<FakeGraphEntryPoint> _factory;

        public Fake(Dictionary<string, string?>? settings = null, bool webhooks = true)
        {
            var configuration = new Dictionary<string, string?>
            {
                ["FakeGraph:Webhook:SentDelay"] = "00:00:00",
                ["FakeGraph:Webhook:DeliveredDelay"] = "00:00:00",
                ["FakeGraph:Webhook:ReadDelay"] = "00:00:00",
            };
            if (webhooks)
            {
                configuration["FakeGraph:Webhook:CallbackUrl"] = CallbackUrl;
                configuration["FakeGraph:Webhook:AppSecret"] = AppSecret;
            }

            foreach (var (key, value) in settings ?? [])
            {
                configuration[key] = value;
            }

            var recorder = Recorder;
            _factory = new WebApplicationFactory<FakeGraphEntryPoint>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration));
                builder.ConfigureTestServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = recorder)));
            });
            Client = _factory.CreateClient();
        }

        public RecordingHandler Recorder { get; } = new();

        public HttpClient Client { get; }

        public async Task<HttpResponseMessage> SendAsync(string to = Recipient, string? callbackData = "cb-1", string? failure = null, string phoneNumberId = "100200300")
        {
            var payload = new Dictionary<string, object?>
            {
                ["messaging_product"] = "whatsapp",
                ["to"] = to,
                ["type"] = "template",
                ["template"] = new
                {
                    name = "booking_status_updated",
                    language = new { code = "en" },
                    components = new[] { new { type = "body", parameters = new[] { new { type = "text", text = "Sam" }, new { type = "text", text = "REF-1" }, new { type = "text", text = "Confirmed" } } } },
                },
            };
            if (callbackData is not null)
            {
                payload["biz_opaque_callback_data"] = callbackData;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/{phoneNumberId}/messages") { Content = JsonContent.Create(payload) };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fake-token");
            if (failure is not null)
            {
                request.Headers.Add(FakeGraphExtensions.FailureHeader, failure);
            }

            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<List<Posted>> WaitForWebhooksAsync(int count) =>
            await Eventually.Async(() => Task.FromResult(Recorder.Posted), posted => posted.Count >= count);

        public async Task<JsonElement> GetJsonAsync(string path) =>
            await Client.GetFromJsonAsync<JsonElement>(path, TestContext.Current.CancellationToken);

        public Task<HttpResponseMessage> PostJsonAsync(string path, object body) =>
            Client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
        }
    }

    private static void ShouldBeSignedWithTheSecret(Posted posted)
    {
        WebhookSignature.TryParseHeader(posted.Signature, out var digest).ShouldBeTrue("the signature header has the sha256= form");
        WebhookSignature.Verify(AppSecret, Encoding.UTF8.GetBytes(posted.Body), digest).ShouldBeTrue("the service accepts the signature");
        posted.Uri.ToString().ShouldBe(CallbackUrl);
    }

    [Fact]
    public async Task An_accepted_message_is_followed_by_signed_sent_delivered_and_read_webhooks_that_echo_the_callback_data()
    {
        using var fake = new Fake();

        var response = await fake.SendAsync(callbackData: "0f8fad5b-d9cb-469f-a165-70867728950e");
        var messageId = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("messages")[0].GetProperty("id").GetString();

        var posted = await fake.WaitForWebhooksAsync(3);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        posted.Select(p => p.Status.GetProperty("status").GetString()).ShouldBe(["sent", "delivered", "read"]);
        foreach (var webhook in posted)
        {
            ShouldBeSignedWithTheSecret(webhook);
            webhook.Json.GetProperty("object").GetString().ShouldBe("whatsapp_business_account");
            webhook.Value.GetProperty("metadata").GetProperty("phone_number_id").GetString().ShouldBe("100200300");
            webhook.Status.GetProperty("id").GetString().ShouldBe(messageId);
            webhook.Status.GetProperty("biz_opaque_callback_data").GetString().ShouldBe("0f8fad5b-d9cb-469f-a165-70867728950e");
            webhook.Status.GetProperty("recipient_id").GetString().ShouldBe("12025550100");
            long.TryParse(webhook.Status.GetProperty("timestamp").GetString(), out _).ShouldBeTrue("epoch seconds as a string");
        }
    }

    [Fact]
    public async Task The_webhook_log_lists_what_was_posted_and_how_the_callback_answered()
    {
        using var fake = new Fake();
        await fake.SendAsync();
        await fake.WaitForWebhooksAsync(3);

        var log = (await fake.GetJsonAsync("/_inspect/webhooks")).EnumerateArray().ToList();

        log.Select(e => e.GetProperty("status").GetString()).ShouldBe(["sent", "delivered", "read"]);
        log.ShouldAllBe(e => e.GetProperty("httpStatus").GetInt32() == 200 && e.GetProperty("kind").GetString() == "status");
    }

    [Theory]
    [InlineData("delivered", new[] { "sent", "delivered" })]
    [InlineData("sent", new[] { "sent" })]
    public async Task An_outcome_ends_the_ladder_where_it_says(string final, string[] expected)
    {
        using var fake = new Fake();
        (await fake.PostJsonAsync("/_inspect/outcomes", new { recipient = Recipient, final })).StatusCode.ShouldBe(HttpStatusCode.Created);

        await fake.SendAsync();
        await fake.WaitForWebhooksAsync(expected.Length);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        fake.Recorder.Posted.Select(p => p.Status.GetProperty("status").GetString()).ShouldBe(expected);
    }

    [Fact]
    public async Task The_none_outcome_accepts_the_message_but_posts_nothing()
    {
        using var fake = new Fake();
        await fake.PostJsonAsync("/_inspect/outcomes", new { recipient = Recipient, final = "none" });

        (await fake.SendAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        fake.Recorder.Posted.ShouldBeEmpty();
        (await fake.GetJsonAsync("/_inspect/messages")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_outcome_posts_one_failed_status_with_the_error()
    {
        using var fake = new Fake();
        await fake.PostJsonAsync("/_inspect/outcomes", new { recipient = Recipient, final = "failed", errorCode = 131047, errorTitle = "Re-engagement message" });

        await fake.SendAsync();
        var posted = await fake.WaitForWebhooksAsync(1);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        fake.Recorder.Posted.Count.ShouldBe(1);
        var status = posted[0].Status;
        status.GetProperty("status").GetString().ShouldBe("failed");
        status.GetProperty("errors")[0].GetProperty("code").GetInt32().ShouldBe(131047);
        status.GetProperty("errors")[0].GetProperty("title").GetString().ShouldBe("Re-engagement message");
        ShouldBeSignedWithTheSecret(posted[0]);
    }

    [Fact]
    public async Task An_outcome_with_a_count_is_used_up_and_the_next_message_is_read_again()
    {
        using var fake = new Fake();
        await fake.PostJsonAsync("/_inspect/outcomes", new { recipient = Recipient, final = "failed", remaining = 1 });

        await fake.SendAsync();
        await fake.SendAsync(callbackData: "second");
        var posted = await fake.WaitForWebhooksAsync(4);

        posted.Select(p => (p.Status.GetProperty("biz_opaque_callback_data").GetString(), p.Status.GetProperty("status").GetString())).ShouldBe(
            [("cb-1", "failed"), ("second", "sent"), ("second", "delivered"), ("second", "read")],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Outcomes_can_also_be_configured_per_recipient()
    {
        using var fake = new Fake(new Dictionary<string, string?>
        {
            ["FakeGraph:Webhook:Outcomes:+12025550143:Final"] = "Failed",
            ["FakeGraph:Webhook:Outcomes:+12025550143:ErrorCode"] = "131026",
        });

        await fake.SendAsync(to: "+12025550143");
        await fake.SendAsync(to: Recipient, callbackData: "other");
        var posted = await fake.WaitForWebhooksAsync(4);

        posted.Where(p => p.Status.GetProperty("biz_opaque_callback_data").GetString() == "cb-1")
            .Select(p => p.Status.GetProperty("status").GetString()).ShouldBe(["failed"]);
        posted.Count(p => p.Status.GetProperty("biz_opaque_callback_data").GetString() == "other").ShouldBe(3);
    }

    [Fact]
    public async Task Without_a_callback_url_nothing_is_posted_and_the_inbound_simulation_says_why()
    {
        using var fake = new Fake(webhooks: false);

        (await fake.SendAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        var inbound = await fake.PostJsonAsync("/_inspect/inbound", new { from = Recipient, phoneNumberId = "100200300" });
        await Task.Delay(200, TestContext.Current.CancellationToken);

        fake.Recorder.Posted.ShouldBeEmpty();
        inbound.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_inbound_simulation_posts_a_signed_inbound_message_and_reports_the_answer_of_the_service()
    {
        using var fake = new Fake();

        var response = await fake.PostJsonAsync("/_inspect/inbound", new { from = Recipient, phoneNumberId = "100200300", text = "Is my booking confirmed?", timestamp = 1772359200 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var delivery = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        delivery.GetProperty("httpStatus").GetInt32().ShouldBe(200);
        delivery.GetProperty("kind").GetString().ShouldBe("inbound");

        var posted = fake.Recorder.Posted.ShouldHaveSingleItem();
        ShouldBeSignedWithTheSecret(posted);
        var message = posted.Value.GetProperty("messages")[0];
        message.GetProperty("from").GetString().ShouldBe("12025550100");
        message.GetProperty("type").GetString().ShouldBe("text");
        message.GetProperty("text").GetProperty("body").GetString().ShouldBe("Is my booking confirmed?");
        message.GetProperty("timestamp").GetString().ShouldBe("1772359200");
        message.GetProperty("id").GetString().ShouldStartWith("wamid.");
        posted.Value.GetProperty("metadata").GetProperty("phone_number_id").GetString().ShouldBe("100200300");
        posted.Value.GetProperty("contacts")[0].GetProperty("wa_id").GetString().ShouldBe("12025550100");
    }

    [Theory]
    [InlineData("", "100200300")]
    [InlineData("12", "100200300")]
    [InlineData("+12025550100", "")]
    public async Task The_inbound_simulation_validates_its_input(string from, string phoneNumberId)
    {
        using var fake = new Fake();

        (await fake.PostJsonAsync("/_inspect/inbound", new { from, phoneNumberId })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        fake.Recorder.Posted.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_callback_that_does_not_answer_ok_is_reported_and_does_not_stop_the_fake()
    {
        using var fake = new Fake();
        fake.Recorder.Answer = HttpStatusCode.InternalServerError;

        var inbound = await fake.PostJsonAsync("/_inspect/inbound", new { from = Recipient, phoneNumberId = "100200300" });
        (await fake.SendAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        await fake.WaitForWebhooksAsync(4);

        inbound.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var log = (await fake.GetJsonAsync("/_inspect/webhooks")).EnumerateArray().ToList();
        log.ShouldAllBe(e => e.GetProperty("httpStatus").GetInt32() == 500 && e.GetProperty("error").GetString()!.Contains("500", StringComparison.Ordinal));
    }

    // ---- accept, then drop the answer ----

    [Fact]
    public async Task Accept_then_drop_records_the_message_posts_its_webhooks_and_breaks_the_connection()
    {
        using var fake = new Fake();

        await Should.ThrowAsync<Exception>(() => fake.SendAsync(callbackData: "cb-drop", failure: FailureModes.DropAfterAccept));

        var messages = (await fake.GetJsonAsync("/_inspect/messages")).EnumerateArray().ToList();
        var message = messages.ShouldHaveSingleItem();
        message.GetProperty("responseDropped").GetBoolean().ShouldBeTrue();
        message.GetProperty("callbackData").GetString().ShouldBe("cb-drop");
        message.GetProperty("messageId").GetString().ShouldStartWith("wamid.");

        var posted = await fake.WaitForWebhooksAsync(3);
        posted.Select(p => p.Status.GetProperty("status").GetString()).ShouldBe(["sent", "delivered", "read"]);
        posted.ShouldAllBe(p => p.Status.GetProperty("id").GetString() == message.GetProperty("messageId").GetString());
        posted.ShouldAllBe(p => p.Status.GetProperty("biz_opaque_callback_data").GetString() == "cb-drop");
    }

    [Fact]
    public async Task Accept_then_drop_can_be_registered_as_a_rule_that_fires_once()
    {
        using var fake = new Fake();
        (await fake.PostJsonAsync("/_inspect/failures", new { mode = FailureModes.DropAfterAccept, remaining = 1 })).StatusCode.ShouldBe(HttpStatusCode.Created);

        await Should.ThrowAsync<Exception>(() => fake.SendAsync(callbackData: "first"));
        var second = await fake.SendAsync(callbackData: "second");

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fake.GetJsonAsync("/_inspect/messages")).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task An_invalid_message_is_rejected_normally_even_when_a_drop_is_requested()
    {
        using var fake = new Fake();

        var response = await fake.SendAsync(to: "not-a-number", failure: FailureModes.DropAfterAccept);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fake.GetJsonAsync("/_inspect/messages")).GetArrayLength().ShouldBe(0);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        fake.Recorder.Posted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Callback_data_over_512_characters_is_rejected_like_the_real_endpoint()
    {
        using var fake = new Fake();

        var response = await fake.SendAsync(callbackData: new string('x', 513));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_callback_data_is_listed_with_the_received_message()
    {
        using var fake = new Fake(webhooks: false);

        await fake.SendAsync(callbackData: "echo-me");

        (await fake.GetJsonAsync("/_inspect/messages"))[0].GetProperty("callbackData").GetString().ShouldBe("echo-me");
    }
}

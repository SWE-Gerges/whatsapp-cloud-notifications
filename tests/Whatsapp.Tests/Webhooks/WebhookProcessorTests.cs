using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Whatsapp.Abstractions;
using Whatsapp.CloudApi;
using Whatsapp.Tests.Support;
using Whatsapp.Webhooks;

namespace Whatsapp.Tests.Webhooks;

public sealed class WebhookProcessorTests : IDisposable
{
    private const string SecretA = "processor-secret-a";
    private const string SecretB = "processor-secret-b";

    private readonly IWebhookTenantDirectory _tenants = Substitute.For<IWebhookTenantDirectory>();
    private readonly IInboundMessageRecorder _inbound = Substitute.For<IInboundMessageRecorder>();
    private readonly IDeliveryReceiptRecorder _receipts = Substitute.For<IDeliveryReceiptRecorder>();
    private readonly TestMeterFactory _meters = new();
    private readonly MetricRecorder _metrics;
    private readonly WhatsappWebhookProcessor _processor;

    public WebhookProcessorTests()
    {
        _tenants.FindByPhoneNumberIdAsync("100200300", Arg.Any<CancellationToken>())
            .Returns(new WebhookTenant("tenant-a", SecretA));
        _tenants.FindByPhoneNumberIdAsync("400500600", Arg.Any<CancellationToken>())
            .Returns(new WebhookTenant("tenant-b", SecretB));
        _inbound.RecordAsync(default!, default).ReturnsForAnyArgs(RecordResult.Recorded);
        _receipts.RecordAsync(default!, default).ReturnsForAnyArgs(RecordResult.Recorded);

        _metrics = new MetricRecorder(_meters);
        _processor = new WhatsappWebhookProcessor(
            _tenants,
            new PhoneNumberNormalizer(),
            _inbound,
            _receipts,
            new WebhookMetrics(_meters),
            NullLogger<WhatsappWebhookProcessor>.Instance);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _meters.Dispose();
    }

    private Task<WebhookResult> HandleAsync(string body, string? secret = SecretA, string? header = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = header ?? (secret is null ? null : WebhookSignature.Sign(secret, bytes));
        return _processor.HandleAsync(bytes, signature, TestContext.Current.CancellationToken);
    }

    private void NothingWasRecorded()
    {
        _inbound.ReceivedCalls().ShouldBeEmpty();
        _receipts.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=0000000000000000000000000000000000000000")]
    [InlineData("sha256=nothex")]
    public async Task A_missing_or_malformed_signature_header_is_rejected_before_anything_else_happens(string? header)
    {
        var result = await HandleAsync(Fixtures.Webhook("inbound-text.json"), secret: null, header: header);

        result.Outcome.ShouldBe(WebhookOutcome.Unauthorized);
        _tenants.ReceivedCalls().ShouldBeEmpty();
        NothingWasRecorded();
    }

    [Fact]
    public async Task A_wrong_secret_or_a_tampered_body_is_rejected_and_nothing_is_recorded()
    {
        var body = Fixtures.Webhook("inbound-text.json");
        var signedWithOtherSecret = await HandleAsync(body, secret: "some-other-secret");

        var bytes = Encoding.UTF8.GetBytes(body);
        var tampered = await _processor.HandleAsync(
            Encoding.UTF8.GetBytes(body.Replace("wamid.INBOUND-TEXT-1", "wamid.INJECTED", StringComparison.Ordinal)),
            WebhookSignature.Sign(SecretA, bytes),
            TestContext.Current.CancellationToken);

        signedWithOtherSecret.Outcome.ShouldBe(WebhookOutcome.Unauthorized);
        tampered.Outcome.ShouldBe(WebhookOutcome.Unauthorized);
        NothingWasRecorded();
        _metrics.Sum(WebhookMetrics.DeliveriesName, ("outcome", "unauthorized")).ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"entry\":[]}")]
    public async Task A_body_that_names_no_phone_number_cannot_be_authenticated(string body)
    {
        var result = await HandleAsync(body);

        result.Outcome.ShouldBe(WebhookOutcome.Unauthorized);
        NothingWasRecorded();
    }

    [Fact]
    public async Task A_delivery_for_an_unknown_phone_number_is_acknowledged_ignored_and_counted()
    {
        var body = Fixtures.Webhook("inbound-text.json", ("100200300", "999000111"));
        _tenants.FindByPhoneNumberIdAsync("999000111", Arg.Any<CancellationToken>()).Returns((WebhookTenant?)null);

        // It is acknowledged even though the signature could not be checked, because no secret exists for it.
        var result = await HandleAsync(body, secret: "irrelevant");

        result.Outcome.ShouldBe(WebhookOutcome.Ignored);
        NothingWasRecorded();
        _metrics.Sum(WebhookMetrics.UnknownPhoneNumberIdsName).ShouldBe(1);
        _metrics.Sum(WebhookMetrics.DeliveriesName, ("outcome", "ignored_unknown_phone_number")).ShouldBe(1);
    }

    [Fact]
    public async Task A_valid_inbound_message_is_recorded_for_the_tenant_that_owns_the_phone_number()
    {
        var result = await HandleAsync(Fixtures.Webhook("inbound-text.json"));

        result.ShouldBe(new WebhookResult(WebhookOutcome.Processed, InboundMessages: 1));
        await _inbound.Received(1).RecordAsync(
            new InboundMessage("tenant-a", "wamid.INBOUND-TEXT-1", "+12025550100", "text", DateTimeOffset.FromUnixTimeSeconds(1772359200)),
            TestContext.Current.CancellationToken);
        _metrics.Sum(WebhookMetrics.InboundMessagesName, ("result", "new")).ShouldBe(1);
    }

    [Fact]
    public async Task A_redelivered_message_is_counted_as_a_duplicate()
    {
        _inbound.RecordAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>()).Returns(RecordResult.Duplicate);

        await HandleAsync(Fixtures.Webhook("inbound-text.json"));

        _metrics.Sum(WebhookMetrics.InboundMessagesName, ("result", "duplicate")).ShouldBe(1);
    }

    [Fact]
    public async Task A_sender_that_is_not_a_phone_number_and_a_missing_timestamp_are_skipped_not_fatal()
    {
        var body = Fixtures.Webhook("inbound-text.json", ("\"from\": \"12025550100\"", "\"from\": \"12\""));
        var noTimestamp = Fixtures.Webhook("inbound-text.json", ("\"timestamp\": \"1772359200\",", string.Empty));

        (await HandleAsync(body)).Outcome.ShouldBe(WebhookOutcome.Processed);
        (await HandleAsync(noTimestamp)).Outcome.ShouldBe(WebhookOutcome.Processed);

        _inbound.ReceivedCalls().ShouldBeEmpty();
        _metrics.Sum(WebhookMetrics.InboundMessagesName, ("result", "invalid_sender")).ShouldBe(1);
        _metrics.Sum(WebhookMetrics.InboundMessagesName, ("result", "missing_timestamp")).ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_status_is_recorded_with_its_error_and_the_echoed_notification_id()
    {
        var result = await HandleAsync(Fixtures.Webhook("status-failed.json"));

        result.ShouldBe(new WebhookResult(WebhookOutcome.Processed, Receipts: 1));
        await _receipts.Received(1).RecordAsync(
            new DeliveryReceiptInput(
                "tenant-a",
                "wamid.OUTBOUND-1",
                "failed",
                DateTimeOffset.FromUnixTimeSeconds(1772359210),
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                131047,
                "Re-engagement message"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Statuses_without_a_timestamp_or_a_usable_callback_id_are_still_recorded()
    {
        var result = await HandleAsync(Fixtures.Webhook("status-unknown.json"));

        result.Receipts.ShouldBe(3);
        var recorded = _receipts.ReceivedCalls().Select(c => (DeliveryReceiptInput)c.GetArguments()[0]!).ToList();
        recorded.Select(r => r.Status).ShouldBe(["deleted", "delivered", "read"]);
        recorded[1].OccurredAt.ShouldBe(WhatsappWebhookProcessor.MissingTimestamp);
        recorded.ShouldAllBe(r => r.NotificationId == null);
    }

    [Fact]
    public async Task Only_the_values_of_tenants_that_exist_are_applied_when_a_delivery_mixes_phone_numbers()
    {
        var known = Fixtures.Webhook("inbound-text.json");
        var mixed = known.Replace(
            "\"changes\": [",
            "\"changes\": [ { \"value\": { \"metadata\": { \"phone_number_id\": \"999000111\" }, \"messages\": [ { \"from\": \"12025550100\", \"id\": \"wamid.OTHER\", \"timestamp\": \"1772359200\", \"type\": \"text\" } ] } },",
            StringComparison.Ordinal);
        _tenants.FindByPhoneNumberIdAsync("999000111", Arg.Any<CancellationToken>()).Returns((WebhookTenant?)null);

        var result = await HandleAsync(mixed);

        result.ShouldBe(new WebhookResult(WebhookOutcome.Processed, InboundMessages: 1));
        await _inbound.Received(1).RecordAsync(Arg.Is<InboundMessage>(m => m.MessageId == "wamid.INBOUND-TEXT-1"), TestContext.Current.CancellationToken);
        _metrics.Sum(WebhookMetrics.UnknownPhoneNumberIdsName).ShouldBe(1);
    }

    [Fact]
    public async Task One_signature_cannot_vouch_for_two_tenants_with_different_secrets()
    {
        var mixed = Fixtures.Webhook("inbound-text.json").Replace(
            "\"changes\": [",
            "\"changes\": [ { \"value\": { \"metadata\": { \"phone_number_id\": \"400500600\" }, \"messages\": [ { \"from\": \"12025550100\", \"id\": \"wamid.B\", \"timestamp\": \"1772359200\", \"type\": \"text\" } ] } },",
            StringComparison.Ordinal);

        (await HandleAsync(mixed, SecretA)).Outcome.ShouldBe(WebhookOutcome.Unauthorized);
        (await HandleAsync(mixed, SecretB)).Outcome.ShouldBe(WebhookOutcome.Unauthorized);
        NothingWasRecorded();
    }

    [Fact]
    public async Task A_storage_failure_is_not_swallowed_so_the_provider_redelivers()
    {
        _inbound.RecordAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database is down"));

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(Fixtures.Webhook("inbound-text.json")));
    }

    [Fact]
    public async Task A_valid_delivery_with_no_events_is_still_a_success()
    {
        var body = "{\"entry\":[{\"changes\":[{\"value\":{\"metadata\":{\"phone_number_id\":\"100200300\"},\"something_new\":1}}]}]}";

        (await HandleAsync(body)).ShouldBe(new WebhookResult(WebhookOutcome.Processed));
    }
}

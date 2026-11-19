using System.Diagnostics.Metrics;

namespace Whatsapp.Webhooks;

/// <summary>
/// Counters for the webhook endpoint. The meter comes from <see cref="IMeterFactory"/>, so each host (and each test)
/// owns its own instruments.
/// </summary>
public sealed class WebhookMetrics
{
    public const string MeterName = "Whatsapp.Notifications";

    public const string DeliveriesName = "whatsapp.webhook.deliveries";
    public const string UnknownPhoneNumberIdsName = "whatsapp.webhook.unknown_phone_number_ids";
    public const string InboundMessagesName = "whatsapp.webhook.inbound_messages";
    public const string ReceiptsName = "whatsapp.webhook.receipts";
    public const string VerificationsName = "whatsapp.webhook.verifications";

    private readonly Counter<long> _deliveries;
    private readonly Counter<long> _unknownPhoneNumberIds;
    private readonly Counter<long> _inboundMessages;
    private readonly Counter<long> _receipts;
    private readonly Counter<long> _verifications;

    public WebhookMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        _deliveries = meter.CreateCounter<long>(DeliveriesName, description: "Webhook deliveries by outcome.");
        _unknownPhoneNumberIds = meter.CreateCounter<long>(UnknownPhoneNumberIdsName, description: "Phone number ids in a delivery that no tenant owns.");
        _inboundMessages = meter.CreateCounter<long>(InboundMessagesName, description: "Inbound messages by result.");
        _receipts = meter.CreateCounter<long>(ReceiptsName, description: "Delivery receipts by status and result.");
        _verifications = meter.CreateCounter<long>(VerificationsName, description: "Verify-token handshakes by result.");
    }

    internal void Delivery(string outcome) => _deliveries.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    internal void UnknownPhoneNumberId(int count) => _unknownPhoneNumberIds.Add(count);

    internal void InboundMessage(string result) => _inboundMessages.Add(1, new KeyValuePair<string, object?>("result", result));

    internal void Receipt(string status, string result) =>
        _receipts.Add(1, new KeyValuePair<string, object?>("status", status), new KeyValuePair<string, object?>("result", result));

    public void Verification(bool accepted) =>
        _verifications.Add(1, new KeyValuePair<string, object?>("result", accepted ? "accepted" : "rejected"));
}

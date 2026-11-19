using Microsoft.Extensions.Logging;
using Whatsapp.Abstractions;

namespace Whatsapp.Webhooks;

public enum WebhookOutcome
{
    /// <summary>The signature held and the events were recorded (there may have been none).</summary>
    Processed = 0,

    /// <summary>The signature is missing, malformed or wrong, or the body names no phone number to pick a secret for.</summary>
    Unauthorized = 1,

    /// <summary>The body names only phone numbers no tenant owns. It is acknowledged so the provider does not retry it.</summary>
    Ignored = 2,
}

public sealed record WebhookResult(WebhookOutcome Outcome, int InboundMessages = 0, int Receipts = 0);

/// <summary>
/// Authenticates and applies one webhook delivery. The signature is checked on the raw bytes before the body is
/// interpreted; the only thing read earlier is the phone number id, which is needed to pick the secret. Work done
/// here is limited to a handful of single-row writes: nothing calls the Cloud API.
/// </summary>
public sealed class WhatsappWebhookProcessor(
    IWebhookTenantDirectory tenants,
    IPhoneNumberNormalizer phones,
    IInboundMessageRecorder inbound,
    IDeliveryReceiptRecorder receipts,
    WebhookMetrics metrics,
    ILogger<WhatsappWebhookProcessor> logger)
{
    /// <summary>Stands in for a status event without a usable timestamp, so a redelivery of it is still a duplicate.</summary>
    public static readonly DateTimeOffset MissingTimestamp = DateTimeOffset.UnixEpoch;

    public async Task<WebhookResult> HandleAsync(ReadOnlyMemory<byte> body, string? signatureHeader, CancellationToken cancellationToken)
    {
        // 1. The header alone is enough to turn away a request that cannot be genuine.
        if (!WebhookSignature.TryParseHeader(signatureHeader, out var presentedDigest))
        {
            return Reject("unauthorized");
        }

        // 2. The tenant comes from the payload, never from a header. Reading the phone number id is all that
        //    happens before the signature is verified.
        var phoneNumberIds = WebhookPayloadParser.ReadPhoneNumberIds(body);
        if (phoneNumberIds.Count == 0)
        {
            return Reject("unauthorized");
        }

        var known = new Dictionary<string, WebhookTenant>(StringComparer.Ordinal);
        var unknown = 0;
        foreach (var phoneNumberId in phoneNumberIds)
        {
            if (await tenants.FindByPhoneNumberIdAsync(phoneNumberId, cancellationToken).ConfigureAwait(false) is { } tenant)
            {
                known[phoneNumberId] = tenant;
            }
            else
            {
                unknown++;
            }
        }

        if (known.Count == 0)
        {
            metrics.UnknownPhoneNumberId(unknown);
            metrics.Delivery("ignored_unknown_phone_number");
            logger.LogInformation("A webhook delivery named {Count} phone number id(s) that no tenant owns and was ignored", unknown);
            return new WebhookResult(WebhookOutcome.Ignored);
        }

        // 3. Constant-time check against the raw bytes. Every tenant the delivery refers to must agree with it,
        //    so one signature cannot vouch for a tenant whose secret it was not made with.
        foreach (var tenant in known.Values)
        {
            if (!WebhookSignature.Verify(tenant.AppSecret, body.Span, presentedDigest))
            {
                logger.LogWarning("A webhook delivery for tenant {TenantId} failed signature verification", tenant.TenantId);
                return Reject("unauthorized");
            }
        }

        metrics.UnknownPhoneNumberId(unknown);

        // 4. Only now is the body interpreted.
        var inboundCount = 0;
        var receiptCount = 0;
        foreach (var value in WebhookPayloadParser.Parse(body))
        {
            if (value.PhoneNumberId is null || !known.TryGetValue(value.PhoneNumberId, out var tenant))
            {
                continue;
            }

            foreach (var message in value.Messages)
            {
                inboundCount += await RecordInboundAsync(tenant, message, cancellationToken).ConfigureAwait(false);
            }

            foreach (var status in value.Statuses)
            {
                receiptCount += await RecordReceiptAsync(tenant, status, cancellationToken).ConfigureAwait(false);
            }
        }

        metrics.Delivery("processed");
        return new WebhookResult(WebhookOutcome.Processed, inboundCount, receiptCount);
    }

    private WebhookResult Reject(string outcome)
    {
        metrics.Delivery(outcome);
        return new WebhookResult(WebhookOutcome.Unauthorized);
    }

    private async Task<int> RecordInboundAsync(WebhookTenant tenant, InboundMessageEvent message, CancellationToken cancellationToken)
    {
        // The wa_id is digits without the plus sign; it goes through the same E.164 path as outbound numbers.
        var sender = phones.Normalize(null, "+" + message.From.Trim().TrimStart('+'));
        if (!sender.IsValid)
        {
            metrics.InboundMessage("invalid_sender");
            logger.LogWarning("An inbound message for tenant {TenantId} has a sender that is not a phone number and was ignored", tenant.TenantId);
            return 0;
        }

        if (message.Timestamp is not { } timestamp)
        {
            metrics.InboundMessage("missing_timestamp");
            logger.LogWarning("An inbound message for tenant {TenantId} has no usable timestamp and was ignored", tenant.TenantId);
            return 0;
        }

        var result = await inbound.RecordAsync(
            new InboundMessage(tenant.TenantId, message.MessageId, sender.E164!, message.Type, timestamp),
            cancellationToken).ConfigureAwait(false);
        metrics.InboundMessage(result == RecordResult.Recorded ? "new" : "duplicate");
        return 1;
    }

    private async Task<int> RecordReceiptAsync(WebhookTenant tenant, StatusEvent status, CancellationToken cancellationToken)
    {
        var notificationId = Guid.TryParse(status.CallbackData, out var parsed) ? parsed : (Guid?)null;

        var result = await receipts.RecordAsync(
            new DeliveryReceiptInput(
                tenant.TenantId,
                status.MessageId,
                status.Status,
                status.Timestamp ?? MissingTimestamp,
                notificationId,
                status.ErrorCode,
                status.ErrorTitle),
            cancellationToken).ConfigureAwait(false);
        metrics.Receipt(status.Status, result == RecordResult.Recorded ? "new" : "duplicate");
        return 1;
    }
}

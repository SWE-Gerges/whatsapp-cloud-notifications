using System.Globalization;
using System.Text.Json;

namespace Whatsapp.Webhooks;

/// <summary>One inbound customer message from <c>entry[].changes[].value.messages[]</c>. The body is not read.</summary>
public sealed record InboundMessageEvent(string MessageId, string From, string Type, DateTimeOffset? Timestamp);

/// <summary>One status event from <c>entry[].changes[].value.statuses[]</c>.</summary>
public sealed record StatusEvent(
    string MessageId,
    string Status,
    DateTimeOffset? Timestamp,
    string? CallbackData,
    int? ErrorCode,
    string? ErrorTitle);

/// <summary>What one <c>changes[].value</c> object contributes: the phone number it concerns and its events.</summary>
public sealed record WebhookValue(
    string? PhoneNumberId,
    IReadOnlyList<InboundMessageEvent> Messages,
    IReadOnlyList<StatusEvent> Statuses);

/// <summary>
/// Reads a webhook body without ever failing on shape. A field or type it does not know is skipped, an item it
/// cannot use is dropped, and the rest of the delivery is still processed.
/// </summary>
public static class WebhookPayloadParser
{
    public const int MaxTypeLength = 32;

    private const int MaxTitleLength = 200;
    private const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>Only the phone number ids, to pick the secret that must verify the signature. Nothing else is read.</summary>
    public static IReadOnlyList<string> ReadPhoneNumberIds(ReadOnlyMemory<byte> body)
    {
        var ids = new List<string>();
        if (!TryOpen(body, out var document))
        {
            return ids;
        }

        using (document)
        {
            foreach (var value in Values(document.RootElement))
            {
                if (value.TryGetProperty("metadata", out var metadata)
                    && String(metadata, "phone_number_id") is { Length: > 0 } id
                    && !ids.Contains(id, StringComparer.Ordinal))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>The events of an authenticated delivery.</summary>
    public static IReadOnlyList<WebhookValue> Parse(ReadOnlyMemory<byte> body)
    {
        var values = new List<WebhookValue>();
        if (!TryOpen(body, out var document))
        {
            return values;
        }

        using (document)
        {
            foreach (var value in Values(document.RootElement))
            {
                var phoneNumberId = value.TryGetProperty("metadata", out var metadata) ? String(metadata, "phone_number_id") : null;
                values.Add(new WebhookValue(phoneNumberId, ReadMessages(value), ReadStatuses(value)));
            }
        }

        return values;
    }

    private static bool TryOpen(ReadOnlyMemory<byte> body, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 32 });
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static IEnumerable<JsonElement> Values(JsonElement root)
    {
        foreach (var entry in Items(root, "entry"))
        {
            foreach (var change in Items(entry, "changes"))
            {
                if (change.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                {
                    yield return value;
                }
            }
        }
    }

    private static List<InboundMessageEvent> ReadMessages(JsonElement value)
    {
        var messages = new List<InboundMessageEvent>();
        foreach (var message in Items(value, "messages"))
        {
            var id = String(message, "id");
            var from = String(message, "from");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from))
            {
                continue;
            }

            messages.Add(new InboundMessageEvent(id, from, ShortType(String(message, "type")), Timestamp(message)));
        }

        return messages;
    }

    private static List<StatusEvent> ReadStatuses(JsonElement value)
    {
        var statuses = new List<StatusEvent>();
        foreach (var status in Items(value, "statuses"))
        {
            var id = String(status, "id");
            var state = String(status, "status");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(state))
            {
                continue;
            }

            int? code = null;
            string? title = null;
            foreach (var error in Items(status, "errors"))
            {
                code = Int(error, "code");
                title = String(error, "title") ?? String(error, "message");
                break;
            }

            statuses.Add(new StatusEvent(
                id,
                state.Trim().ToLowerInvariant(),
                Timestamp(status),
                String(status, "biz_opaque_callback_data"),
                code,
                title is { Length: > MaxTitleLength } ? title[..MaxTitleLength] : title));
        }

        return statuses;
    }

    private static string ShortType(string? type)
    {
        var trimmed = type?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "unknown";
        }

        return trimmed.Length > MaxTypeLength ? trimmed[..MaxTypeLength] : trimmed;
    }

    /// <summary>The objects of an array property. A missing property, another kind of value or a non-object item yields nothing.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                yield return item;
            }
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>The provider sends epoch seconds as a string; a plain number is accepted as well.</summary>
    private static DateTimeOffset? Timestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("timestamp", out var property))
        {
            return null;
        }

        long seconds;
        switch (property.ValueKind)
        {
            case JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                seconds = parsed;
                break;
            case JsonValueKind.Number when property.TryGetInt64(out var number):
                seconds = number;
                break;
            default:
                return null;
        }

        // Anything outside the representable range is as unusable as a missing timestamp.
        return seconds is >= 0 and <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}

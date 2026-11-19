using System.Net;
using System.Text.Json;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>Parses Graph API response bodies. Parsing never throws on malformed input.</summary>
internal static class GraphResponseParser
{
    /// <summary>Reads <c>messages[0].id</c> and <c>contacts[0].wa_id</c> from a success body.</summary>
    /// <exception cref="WhatsappApiException">The body does not contain a message id.</exception>
    public static WhatsappSendResult ParseSuccess(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && TryFirstString(root, "messages", "id", out var messageId))
            {
                var hasContact = TryFirstString(root, "contacts", "wa_id", out var waId);
                return new WhatsappSendResult(messageId, hasContact ? waId : null);
            }
        }
        catch (JsonException)
        {
            // Falls through to the typed exception below.
        }

        throw new WhatsappApiException(
            WhatsappErrorKind.InvalidResponse,
            "The Cloud API answered with a success status but no message id.",
            statusCode,
            outcomeUnknown: true);
    }

    /// <summary>Reads the <c>error</c> envelope. Returns null when the body is not a Graph error.</summary>
    public static GraphError? ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new GraphError(
                GetString(error, "message"),
                GetString(error, "type"),
                GetInt(error, "code"),
                GetInt(error, "error_subcode"),
                GetString(error, "fbtrace_id"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryFirstString(JsonElement root, string arrayName, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(arrayName, out var array)
            || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() == 0)
        {
            return false;
        }

        var first = array[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
}

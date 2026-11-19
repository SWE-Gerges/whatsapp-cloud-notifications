using System.Text.Encodings.Web;
using System.Text.Json;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>Builds the JSON body of <c>POST /{phone-number-id}/messages</c>.</summary>
internal static class GraphPayloadBuilder
{
    // Relaxed escaping keeps '+' and non-Latin text readable; the body is never embedded in HTML.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    public static string Build(WhatsappMessage message) => message switch
    {
        WhatsappTextMessage text => BuildText(text),
        WhatsappTemplateMessage template => BuildTemplate(template),
        _ => throw new ArgumentException($"Unsupported message type {message.GetType().Name}.", nameof(message)),
    };

    private static string BuildText(WhatsappTextMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("messaging_product", "whatsapp");
            writer.WriteString("to", message.To);
            writer.WriteString("type", "text");
            writer.WriteStartObject("text");
            writer.WriteString("body", message.Body);
            writer.WriteEndObject();
            WriteCallbackData(writer, message);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static string BuildTemplate(WhatsappTemplateMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("messaging_product", "whatsapp");
            writer.WriteString("to", message.To);
            writer.WriteString("type", "template");
            writer.WriteStartObject("template");
            writer.WriteString("name", message.TemplateName);
            writer.WriteStartObject("language");
            writer.WriteString("code", message.LanguageCode);
            writer.WriteEndObject();

            if (message.Parameters.Count > 0)
            {
                writer.WriteStartArray("components");
                writer.WriteStartObject();
                writer.WriteString("type", "body");
                writer.WriteStartArray("parameters");
                foreach (var parameter in message.Parameters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", parameter);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            WriteCallbackData(writer, message);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void WriteCallbackData(Utf8JsonWriter writer, WhatsappMessage message)
    {
        if (!string.IsNullOrEmpty(message.CallbackData))
        {
            writer.WriteString("biz_opaque_callback_data", message.CallbackData);
        }
    }
}

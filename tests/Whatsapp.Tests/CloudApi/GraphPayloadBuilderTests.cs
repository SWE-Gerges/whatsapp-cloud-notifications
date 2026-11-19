using Whatsapp.Abstractions;
using Whatsapp.CloudApi;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.CloudApi;

public class GraphPayloadBuilderTests
{
    [Fact]
    public void Text_message_matches_golden_json()
    {
        var json = GraphPayloadBuilder.Build(new WhatsappTextMessage(TestData.Recipient, "Hello there"));

        json.ShouldBe("""{"messaging_product":"whatsapp","to":"+12025550100","type":"text","text":{"body":"Hello there"}}""");
    }

    [Fact]
    public void Template_message_matches_golden_json()
    {
        var json = GraphPayloadBuilder.Build(new WhatsappTemplateMessage(
            TestData.Recipient,
            "booking_created",
            "en",
            ["Sam", "Acme Travel"]));

        json.ShouldBe(
            """{"messaging_product":"whatsapp","to":"+12025550100","type":"template","template":{"name":"booking_created","language":{"code":"en"},"components":[{"type":"body","parameters":[{"type":"text","text":"Sam"},{"type":"text","text":"Acme Travel"}]}]}}""");
    }

    private const string NotificationId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Fact]
    public void Text_message_carries_the_notification_id_as_biz_opaque_callback_data()
    {
        var json = GraphPayloadBuilder.Build(new WhatsappTextMessage(TestData.Recipient, "Hello there") { CallbackData = NotificationId });

        json.ShouldBe(
            $$"""{"messaging_product":"whatsapp","to":"+12025550100","type":"text","text":{"body":"Hello there"},"biz_opaque_callback_data":"{{NotificationId}}"}""");
    }

    [Fact]
    public void Template_message_carries_the_notification_id_as_biz_opaque_callback_data()
    {
        var json = GraphPayloadBuilder.Build(new WhatsappTemplateMessage(TestData.Recipient, "booking_created", "en", ["Sam", "Acme Travel"])
        {
            CallbackData = NotificationId,
        });

        json.ShouldBe(
            $$"""{"messaging_product":"whatsapp","to":"+12025550100","type":"template","template":{"name":"booking_created","language":{"code":"en"},"components":[{"type":"body","parameters":[{"type":"text","text":"Sam"},{"type":"text","text":"Acme Travel"}]}]},"biz_opaque_callback_data":"{{NotificationId}}"}""");
    }

    [Fact]
    public void Template_without_parameters_has_no_components()
    {
        var json = GraphPayloadBuilder.Build(new WhatsappTemplateMessage(TestData.Recipient, "welcome", "ar", []));

        json.ShouldBe(
            """{"messaging_product":"whatsapp","to":"+12025550100","type":"template","template":{"name":"welcome","language":{"code":"ar"}}}""");
    }

    [Fact]
    public void Non_latin_text_and_quotes_are_written_as_valid_json()
    {
        var json = GraphPayloadBuilder.Build(new WhatsappTextMessage(TestData.Recipient, "مرحبا \"Sam\""));

        json.ShouldContain("مرحبا");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        document.RootElement.GetProperty("text").GetProperty("body").GetString().ShouldBe("مرحبا \"Sam\"");
    }
}

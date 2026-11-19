using System.Text;
using Whatsapp.Tests.Support;
using Whatsapp.Webhooks;

namespace Whatsapp.Tests.Webhooks;

/// <summary>Golden fixtures in the shape the provider delivers. A new field or type must never fail a delivery.</summary>
public sealed class WebhookPayloadParserTests
{
    private static ReadOnlyMemory<byte> Body(string text) => Encoding.UTF8.GetBytes(text);

    private static readonly DateTimeOffset Noon = DateTimeOffset.FromUnixTimeSeconds(1772359200);

    [Fact]
    public void A_text_message_is_read_without_its_body()
    {
        var value = WebhookPayloadParser.Parse(Body(Fixtures.Webhook("inbound-text.json"))).ShouldHaveSingleItem();

        value.PhoneNumberId.ShouldBe("100200300");
        value.Statuses.ShouldBeEmpty();
        value.Messages.ShouldHaveSingleItem().ShouldBe(
            new InboundMessageEvent("wamid.INBOUND-TEXT-1", "12025550100", "text", Noon));
    }

    [Fact]
    public void An_image_message_is_read_by_its_type_only()
    {
        var value = WebhookPayloadParser.Parse(Body(Fixtures.Webhook("inbound-image.json"))).ShouldHaveSingleItem();

        value.Messages.ShouldHaveSingleItem().ShouldBe(
            new InboundMessageEvent("wamid.INBOUND-IMAGE-1", "12025550100", "image", Noon.AddSeconds(60)));
    }

    [Fact]
    public void Unknown_types_unknown_fields_and_unusable_items_do_not_fail_the_delivery()
    {
        var values = WebhookPayloadParser.Parse(Body(Fixtures.Webhook("inbound-unknown-type.json")));

        values.Count.ShouldBe(2);
        values[0].PhoneNumberId.ShouldBe("100200300");
        values[0].Messages.ShouldBe(
        [
            new InboundMessageEvent("wamid.INBOUND-HOLOGRAM-1", "12025550100", "hologram", Noon.AddSeconds(120)),
            new InboundMessageEvent("wamid.INBOUND-REACTION-1", "12025550100", "reaction", Noon.AddSeconds(130)),
        ]);

        // A change of another field has no metadata and no events, and is simply empty.
        values[1].PhoneNumberId.ShouldBeNull();
        values[1].Messages.ShouldBeEmpty();
        values[1].Statuses.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("status-sent.json", "sent", 1)]
    [InlineData("status-delivered.json", "delivered", 5)]
    [InlineData("status-read.json", "read", 30)]
    public void Each_status_is_read_with_its_timestamp_and_the_echoed_notification_id(string fixture, string status, int secondsAfterNoon)
    {
        var value = WebhookPayloadParser.Parse(Body(Fixtures.Webhook(fixture))).ShouldHaveSingleItem();

        value.Messages.ShouldBeEmpty();
        value.Statuses.ShouldHaveSingleItem().ShouldBe(new StatusEvent(
            "wamid.OUTBOUND-1",
            status,
            Noon.AddSeconds(secondsAfterNoon),
            "11111111-2222-3333-4444-555555555555",
            null,
            null));
    }

    [Fact]
    public void A_failed_status_carries_the_error_code_and_title()
    {
        var value = WebhookPayloadParser.Parse(Body(Fixtures.Webhook("status-failed.json"))).ShouldHaveSingleItem();

        value.Statuses.ShouldHaveSingleItem().ShouldBe(new StatusEvent(
            "wamid.OUTBOUND-1",
            "failed",
            Noon.AddSeconds(10),
            "11111111-2222-3333-4444-555555555555",
            131047,
            "Re-engagement message"));
    }

    [Fact]
    public void Unknown_statuses_and_malformed_items_are_kept_or_dropped_one_by_one()
    {
        var value = WebhookPayloadParser.Parse(Body(Fixtures.Webhook("status-unknown.json"))).ShouldHaveSingleItem();

        value.Statuses.ShouldBe(
        [
            new StatusEvent("wamid.OUTBOUND-2", "deleted", Noon.AddSeconds(40), null, null, null),
            new StatusEvent("wamid.OUTBOUND-3", "delivered", null, null, null, null),
            new StatusEvent("wamid.OUTBOUND-4", "read", Noon.AddSeconds(60), "not-a-guid", null, null),
        ]);
    }

    [Fact]
    public void The_phone_number_ids_are_read_without_interpreting_anything_else()
    {
        WebhookPayloadParser.ReadPhoneNumberIds(Body(Fixtures.Webhook("inbound-unknown-type.json"))).ShouldBe(["100200300"]);
        WebhookPayloadParser.ReadPhoneNumberIds(Body(Fixtures.Webhook("status-failed.json"))).ShouldBe(["100200300"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"entry\": \"nope\"}")]
    [InlineData("{\"entry\": [1, \"a\", null, {\"changes\": 5}, {\"changes\": [{\"value\": 7}]}]}")]
    [InlineData("{\"entry\":[{\"changes\":[{\"value\":{\"metadata\":{\"phone_number_id\":12345},\"messages\":\"x\",\"statuses\":{}}}]}]}")]
    public void A_body_of_any_other_shape_yields_no_events_and_never_throws(string body)
    {
        WebhookPayloadParser.ReadPhoneNumberIds(Body(body)).ShouldBeEmpty();

        WebhookPayloadParser.Parse(Body(body)).SelectMany(v => v.Messages).ShouldBeEmpty();
        WebhookPayloadParser.Parse(Body(body)).SelectMany(v => v.Statuses).ShouldBeEmpty();
    }

    [Fact]
    public void A_very_long_type_is_shortened()
    {
        var body = Fixtures.Webhook("inbound-text.json", ("\"type\": \"text\"", $"\"type\": \"{new string('t', 200)}\""));

        WebhookPayloadParser.Parse(Body(body)).Single().Messages.Single().Type.Length.ShouldBe(WebhookPayloadParser.MaxTypeLength);
    }
}

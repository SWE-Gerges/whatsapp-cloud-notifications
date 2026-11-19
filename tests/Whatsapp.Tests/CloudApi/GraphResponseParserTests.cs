using System.Net;
using Whatsapp.Abstractions;
using Whatsapp.CloudApi;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.CloudApi;

public class GraphResponseParserTests
{
    [Fact]
    public void Success_body_yields_message_id_and_wa_id()
    {
        var result = GraphResponseParser.ParseSuccess(TestData.SuccessBody, HttpStatusCode.OK);

        result.MessageId.ShouldBe("wamid.TEST-1");
        result.ContactWaId.ShouldBe("12025550100");
    }

    [Fact]
    public void Success_body_without_contacts_still_parses()
    {
        var result = GraphResponseParser.ParseSuccess("""{"messages":[{"id":"wamid.X"}]}""", HttpStatusCode.OK);

        result.MessageId.ShouldBe("wamid.X");
        result.ContactWaId.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"messages":[{"id":""}]}""")]
    [InlineData("""{"messages":[{"nope":1}]}""")]
    public void Success_body_without_a_message_id_is_an_invalid_response(string body)
    {
        var ex = Should.Throw<WhatsappApiException>(() => GraphResponseParser.ParseSuccess(body, HttpStatusCode.OK));

        ex.Kind.ShouldBe(WhatsappErrorKind.InvalidResponse);
        ex.IsTransient.ShouldBeFalse();
    }

    [Fact]
    public void Error_envelope_is_parsed_with_all_fields()
    {
        var error = GraphResponseParser.ParseError(
            """{"error":{"message":"Message undeliverable","type":"OAuthException","code":131026,"error_subcode":2494073,"fbtrace_id":"AbC123","error_data":{"details":"x"}}}""");

        error.ShouldNotBeNull();
        error.Message.ShouldBe("Message undeliverable");
        error.Type.ShouldBe("OAuthException");
        error.Code.ShouldBe(131026);
        error.Subcode.ShouldBe(2494073);
        error.FbTraceId.ShouldBe("AbC123");
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[]")]
    [InlineData("""{"error":"plain string"}""")]
    [InlineData("""{"other":1}""")]
    public void Non_graph_error_bodies_yield_null(string body)
    {
        GraphResponseParser.ParseError(body).ShouldBeNull();
    }

    [Fact]
    public void Error_envelope_with_missing_fields_still_parses()
    {
        var error = GraphResponseParser.ParseError("""{"error":{"code":4}}""");

        error.ShouldNotBeNull();
        error.Code.ShouldBe(4);
        error.Message.ShouldBeNull();
        error.Subcode.ShouldBeNull();
    }
}

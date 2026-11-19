using System.Text;
using Whatsapp.Webhooks;

namespace Whatsapp.Tests.Webhooks;

public sealed class WebhookSignatureTests
{
    // HMAC-SHA256 test vector: key "key", message "The quick brown fox jumps over the lazy dog".
    private const string VectorSecret = "key";
    private const string VectorBody = "The quick brown fox jumps over the lazy dog";
    private const string VectorHex = "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8";

    [Fact]
    public void Sign_produces_the_documented_header_format()
    {
        WebhookSignature.Sign(VectorSecret, Encoding.UTF8.GetBytes(VectorBody)).ShouldBe("sha256=" + VectorHex);
    }

    [Fact]
    public void A_header_signed_with_the_same_secret_and_body_verifies()
    {
        var body = Encoding.UTF8.GetBytes(VectorBody);
        WebhookSignature.TryParseHeader(WebhookSignature.Sign(VectorSecret, body), out var digest).ShouldBeTrue();

        WebhookSignature.Verify(VectorSecret, body, digest).ShouldBeTrue();
    }

    [Fact]
    public void Upper_case_hex_digits_are_accepted()
    {
        var body = Encoding.UTF8.GetBytes(VectorBody);

        WebhookSignature.TryParseHeader("sha256=" + VectorHex.ToUpperInvariant(), out var digest).ShouldBeTrue();

        WebhookSignature.Verify(VectorSecret, body, digest).ShouldBeTrue();
    }

    [Fact]
    public void A_different_body_or_secret_does_not_verify()
    {
        var body = Encoding.UTF8.GetBytes(VectorBody);
        WebhookSignature.TryParseHeader("sha256=" + VectorHex, out var digest).ShouldBeTrue();

        WebhookSignature.Verify(VectorSecret, Encoding.UTF8.GetBytes(VectorBody + " "), digest).ShouldBeFalse();
        WebhookSignature.Verify("other", body, digest).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8")]
    [InlineData("sha1=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8")]
    [InlineData("SHA256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8")]
    [InlineData("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd")]
    [InlineData("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8aa")]
    [InlineData("sha256=g7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8")]
    [InlineData("sha256= f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd")]
    public void A_missing_or_malformed_header_is_rejected(string? header)
    {
        WebhookSignature.TryParseHeader(header, out var digest).ShouldBeFalse();
        digest.ShouldBeEmpty();
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Whatsapp.Webhooks;

/// <summary>
/// The <c>X-Hub-Signature-256</c> scheme: <c>sha256=</c> followed by the hex HMAC-SHA256 of the exact bytes of the
/// request body, keyed with the app secret.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Hub-Signature-256";

    private const string Prefix = "sha256=";
    private const int DigestLength = 32;

    /// <summary>Builds the header value for a body. Used by the fake Graph server and by tests.</summary>
    public static string Sign(string appSecret, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSecret);

        Span<byte> digest = stackalloc byte[DigestLength];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body, digest);
        return Prefix + Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Reads the digest out of a header value without looking at the body. Anything that is not
    /// <c>sha256=</c> plus exactly 64 hex digits is rejected.
    /// </summary>
    public static bool TryParseHeader(string? header, out byte[] digest)
    {
        digest = [];
        if (header is null
            || header.Length != Prefix.Length + (DigestLength * 2)
            || !header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = header.AsSpan(Prefix.Length);
        foreach (var character in hex)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        digest = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>Checks the digest against the raw body in constant time.</summary>
    public static bool Verify(string appSecret, ReadOnlySpan<byte> body, byte[] presentedDigest)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSecret);
        ArgumentNullException.ThrowIfNull(presentedDigest);

        Span<byte> expected = stackalloc byte[DigestLength];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presentedDigest);
    }
}

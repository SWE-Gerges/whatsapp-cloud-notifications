using System.Text.RegularExpressions;

namespace Whatsapp.Abstractions;

/// <summary>Masking helpers so that phone numbers and tokens never reach logs or stored errors.</summary>
public static partial class SensitiveData
{
    /// <summary>Masks the middle of a phone number: <c>+15550100123</c> becomes <c>+1555****0123</c>.</summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone))
        {
            return "****";
        }

        const int keepStart = 5;
        const int keepEnd = 4;
        return phone.Length <= keepStart + keepEnd
            ? "****"
            : string.Concat(phone.AsSpan(0, keepStart), "****", phone.AsSpan(phone.Length - keepEnd));
    }

    /// <summary>Replaces every phone-number-like sequence inside free text with its masked form.</summary>
    public static string MaskPhonesInText(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : PhoneLikeSequence().Replace(text, match => MaskPhone(match.Value));

    /// <summary>Tokens are never shown, not even partially.</summary>
    public static string RedactToken(string? token) => string.IsNullOrEmpty(token) ? "(empty)" : "***";

    [GeneratedRegex(@"\+?\d{8,15}")]
    private static partial Regex PhoneLikeSequence();
}

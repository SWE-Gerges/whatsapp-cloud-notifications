namespace Whatsapp.Abstractions;

public interface IPhoneNumberNormalizer
{
    /// <summary>
    /// Normalises a phone number to E.164. With a calling code the number is
    /// <c>+{callingCode}{nationalNumber}</c>; without one the national number is parsed
    /// using <paramref name="defaultRegion"/> (an ISO 3166-1 alpha-2 code) unless it already starts with a plus sign.
    /// </summary>
    PhoneNormalization Normalize(string? callingCode, string? nationalNumber, string? defaultRegion = null);
}

/// <summary>Outcome of a normalisation attempt. Invalid input never throws.</summary>
public sealed record PhoneNormalization
{
    private PhoneNormalization(string? e164, string? region, string? failureReason)
    {
        E164 = e164;
        Region = region;
        FailureReason = failureReason;
    }

    public bool IsValid => E164 is not null;

    public string? E164 { get; }

    /// <summary>ISO region the number belongs to, when it could be determined.</summary>
    public string? Region { get; }

    public string? FailureReason { get; }

    public static PhoneNormalization Valid(string e164, string? region) => new(e164, region, null);

    public static PhoneNormalization Invalid(string reason) => new(null, null, reason);
}

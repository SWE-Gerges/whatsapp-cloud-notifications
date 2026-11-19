using PhoneNumbers;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>E.164 normalisation backed by libphonenumber.</summary>
internal sealed class PhoneNumberNormalizer : IPhoneNumberNormalizer
{
    private const string UnknownRegion = "ZZ";

    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    public PhoneNormalization Normalize(string? callingCode, string? nationalNumber, string? defaultRegion = null)
    {
        if (string.IsNullOrWhiteSpace(nationalNumber))
        {
            return PhoneNormalization.Invalid("The phone number is empty.");
        }

        try
        {
            return string.IsNullOrWhiteSpace(callingCode)
                ? NormalizeWithoutCallingCode(nationalNumber, defaultRegion)
                : NormalizeWithCallingCode(callingCode, nationalNumber);
        }
        catch (NumberParseException ex)
        {
            return PhoneNormalization.Invalid($"The phone number could not be parsed ({ex.ErrorType}).");
        }
    }

    private static PhoneNormalization NormalizeWithCallingCode(string callingCode, string nationalNumber)
    {
        var code = callingCode.Trim().TrimStart('+');
        if (code.Length is 0 or > 3 || !code.All(char.IsAsciiDigit))
        {
            return PhoneNormalization.Invalid($"The country calling code '{callingCode}' is not valid.");
        }

        var callingCodeValue = int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
        var region = Util.GetRegionCodeForCountryCode(callingCodeValue);
        if (region == UnknownRegion)
        {
            return PhoneNormalization.Invalid($"The country calling code '{code}' is not assigned.");
        }

        // Parsing the national part with the main region of the calling code strips national trunk prefixes.
        var parsed = nationalNumber.TrimStart().StartsWith('+')
            ? Util.Parse(nationalNumber, null)
            : Util.Parse(nationalNumber, region);

        if (parsed.CountryCode != callingCodeValue)
        {
            return PhoneNormalization.Invalid("The phone number does not belong to the given country calling code.");
        }

        return Finish(parsed);
    }

    private static PhoneNormalization NormalizeWithoutCallingCode(string number, string? defaultRegion)
    {
        var trimmed = number.Trim();
        if (!trimmed.StartsWith('+') && string.IsNullOrWhiteSpace(defaultRegion))
        {
            return PhoneNormalization.Invalid(
                "The phone number has no country calling code and no default region was given.");
        }

        var parsed = Util.Parse(trimmed, trimmed.StartsWith('+') ? null : defaultRegion!.Trim().ToUpperInvariant());
        return Finish(parsed);
    }

    private static PhoneNormalization Finish(PhoneNumber parsed)
    {
        if (!Util.IsValidNumber(parsed))
        {
            return PhoneNormalization.Invalid("The phone number is not a valid number for its country.");
        }

        var region = Util.GetRegionCodeForNumber(parsed);
        return PhoneNormalization.Valid(
            Util.Format(parsed, PhoneNumberFormat.E164),
            string.IsNullOrEmpty(region) || region == UnknownRegion ? null : region);
    }
}

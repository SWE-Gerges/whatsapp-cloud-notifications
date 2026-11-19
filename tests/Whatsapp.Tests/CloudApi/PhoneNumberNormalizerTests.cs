using PhoneNumbers;
using Whatsapp.CloudApi;

namespace Whatsapp.Tests.CloudApi;

public class PhoneNumberNormalizerTests
{
    private readonly PhoneNumberNormalizer _normalizer = new();

    [Theory]
    [InlineData("1", "2025550100", "+12025550100", "US")]
    [InlineData("+1", "(202) 555-0143", "+12025550143", "US")]
    [InlineData("44", "020 7946 0958", "+442079460958", "GB")]
    [InlineData("44", "2079460958", "+442079460958", "GB")]
    public void Calling_code_and_national_number_are_normalised_to_e164(
        string callingCode, string national, string expected, string expectedRegion)
    {
        var result = _normalizer.Normalize(callingCode, national);

        result.IsValid.ShouldBeTrue(result.FailureReason);
        result.E164.ShouldBe(expected);
        result.Region.ShouldBe(expectedRegion);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("GB")]
    [InlineData("EG")]
    [InlineData("AE")]
    [InlineData("DE")]
    [InlineData("FR")]
    [InlineData("SA")]
    public void Example_mobile_numbers_of_each_region_are_accepted_with_their_calling_code(string region)
    {
        var util = PhoneNumberUtil.GetInstance();
        var example = util.GetExampleNumberForType(region, PhoneNumberType.MOBILE)!;
        var national = util.Format(example, PhoneNumberFormat.NATIONAL);

        var result = _normalizer.Normalize(example.CountryCode.ToString(System.Globalization.CultureInfo.InvariantCulture), national);

        result.IsValid.ShouldBeTrue(result.FailureReason);
        result.E164.ShouldBe(util.Format(example, PhoneNumberFormat.E164));
    }

    [Theory]
    [InlineData("US")]
    [InlineData("GB")]
    [InlineData("EG")]
    [InlineData("AE")]
    [InlineData("DE")]
    [InlineData("FR")]
    [InlineData("SA")]
    public void Example_mobile_numbers_of_each_region_are_accepted_with_a_default_region(string region)
    {
        var util = PhoneNumberUtil.GetInstance();
        var example = util.GetExampleNumberForType(region, PhoneNumberType.MOBILE)!;
        var national = util.Format(example, PhoneNumberFormat.NATIONAL);

        var result = _normalizer.Normalize(null, national, region);

        result.IsValid.ShouldBeTrue(result.FailureReason);
        result.E164.ShouldBe(util.Format(example, PhoneNumberFormat.E164));
    }

    [Fact]
    public void International_number_without_calling_code_or_region_is_parsed()
    {
        var util = PhoneNumberUtil.GetInstance();
        var e164 = util.Format(util.GetExampleNumberForType("EG", PhoneNumberType.MOBILE)!, PhoneNumberFormat.E164);

        var result = _normalizer.Normalize(null, e164);

        result.IsValid.ShouldBeTrue();
        result.E164.ShouldBe(e164);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("1", "", null)]
    [InlineData("1", "   ", null)]
    [InlineData("1", "123", null)]
    [InlineData("1", "5555550100", null)]
    [InlineData("44", "1234", null)]
    [InlineData("abc", "2025550100", null)]
    [InlineData("1234", "2025550100", null)]
    [InlineData("999", "123456789", null)]
    [InlineData(null, "2025550100", null)]
    [InlineData(null, "not a number", "US")]
    [InlineData("1", "+442079460958", null)]
    public void Invalid_numbers_are_rejected_with_a_reason_and_never_throw(string? callingCode, string? national, string? region)
    {
        var result = _normalizer.Normalize(callingCode, national, region);

        result.IsValid.ShouldBeFalse();
        result.E164.ShouldBeNull();
        result.FailureReason.ShouldNotBeNullOrWhiteSpace();
    }
}

using Whatsapp.Abstractions;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.CloudApi;

public class SensitiveDataTests
{
    [Theory]
    [InlineData("+15555550100", "+1555****0100")]
    [InlineData("+12025550143", "+1202****0143")]
    [InlineData("+442079460958", "+4420****0958")]
    [InlineData("12345", "****")]
    [InlineData("", "****")]
    [InlineData(null, "****")]
    public void Phone_numbers_keep_only_prefix_and_suffix(string? input, string expected)
    {
        SensitiveData.MaskPhone(input).ShouldBe(expected);
    }

    [Fact]
    public void Phone_numbers_inside_free_text_are_masked()
    {
        SensitiveData.MaskPhonesInText("Recipient +12025550100 and 12025550143 failed, code 131026")
            .ShouldBe("Recipient +1202****0100 and 12025****0143 failed, code 131026");
    }

    [Fact]
    public void Tokens_are_never_shown()
    {
        SensitiveData.RedactToken("abc123").ShouldBe("***");
        SensitiveData.RedactToken("").ShouldBe("(empty)");
    }

    [Fact]
    public void Tenant_options_do_not_print_the_access_token()
    {
        var text = TestData.Tenant().ToString();

        text.ShouldNotContain(TestData.Token);
        text.ShouldContain("TenantId = tenant-a");
        text.ShouldContain("AccessToken = ***");
    }
}

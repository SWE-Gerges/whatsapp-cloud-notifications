using Whatsapp.Abstractions;

namespace Whatsapp.Tests.Support;

internal static class TestData
{
    /// <summary>A valid, fictional US number (555-01xx exchange inside a real area code).</summary>
    public const string Recipient = "+12025550100";

    public const string OtherRecipient = "+12025550143";

    public const string Token = "token-for-tests-only";

    public const string SuccessBody =
        """{"messaging_product":"whatsapp","contacts":[{"input":"+12025550100","wa_id":"12025550100"}],"messages":[{"id":"wamid.TEST-1"}]}""";

    public static WhatsappTenantOptions Tenant(
        string tenantId = "tenant-a",
        string token = Token,
        bool enabled = true,
        bool serviceWindowEnabled = true,
        string language = "en") => new()
    {
        TenantId = tenantId,
        Enabled = enabled,
        PhoneNumberId = "100200300",
        AccessToken = token,
        BaseUrl = "https://graph.example.com/v1",
        DefaultTemplateLanguage = language,
        ServiceWindowEnabled = serviceWindowEnabled,
    };

    public static string ErrorBody(int code, string message = "Something failed", int? subcode = null, string type = "OAuthException")
    {
        var sub = subcode is null ? string.Empty : $",\"error_subcode\":{subcode}";
        return $$$"""{"error":{"message":"{{{message}}}","type":"{{{type}}}","code":{{{code}}}{{{sub}}},"fbtrace_id":"trace-123"}}""";
    }
}

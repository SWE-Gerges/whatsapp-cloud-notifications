using System.Text;

namespace Whatsapp.Abstractions;

/// <summary>Immutable per-call settings for one tenant.</summary>
public sealed record WhatsappTenantOptions
{
    public required string TenantId { get; init; }

    public required bool Enabled { get; init; }

    public required string PhoneNumberId { get; init; }

    /// <summary>Bearer token. Never log or serialise this value.</summary>
    public required string AccessToken { get; init; }

    /// <summary>Graph API base URL including the API version, without a trailing slash.</summary>
    public required string BaseUrl { get; init; }

    public string DefaultTemplateLanguage { get; init; } = "en";

    /// <summary>When true, free-form text is only sent inside the 24 hour service window.</summary>
    public bool ServiceWindowEnabled { get; init; } = true;

    // The generated ToString would print the token, so the record prints its own members.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("TenantId = ").Append(TenantId)
            .Append(", Enabled = ").Append(Enabled)
            .Append(", PhoneNumberId = ").Append(PhoneNumberId)
            .Append(", AccessToken = ***")
            .Append(", BaseUrl = ").Append(BaseUrl)
            .Append(", DefaultTemplateLanguage = ").Append(DefaultTemplateLanguage)
            .Append(", ServiceWindowEnabled = ").Append(ServiceWindowEnabled);
        return true;
    }
}

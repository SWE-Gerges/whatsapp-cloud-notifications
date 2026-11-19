using Microsoft.Extensions.Options;

namespace Whatsapp.CloudApi;

/// <summary>Tenant settings read from configuration. Secrets are referenced by name, never stored here.</summary>
public sealed class WhatsappTenantsOptions
{
    public const string SectionName = "Whatsapp";

    public Dictionary<string, WhatsappTenantSettings> Tenants { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WhatsappTenantSettings
{
    /// <summary>A disabled tenant is configured but never sends: its messages are skipped.</summary>
    public bool Enabled { get; set; } = true;

    public string? PhoneNumberId { get; set; }

    /// <summary>Name of the secret holding the access token (resolved through <c>ISecretProvider</c>).</summary>
    public string? AccessTokenSecret { get; set; }

    /// <summary>Graph API base URL including the version, for example the value given by the provider documentation.</summary>
    public string? BaseUrl { get; set; }

    public string DefaultTemplateLanguage { get; set; } = "en";

    public bool ServiceWindowEnabled { get; set; } = true;

    /// <summary>
    /// Name of the secret holding the app secret that signs webhook deliveries (<c>X-Hub-Signature-256</c>).
    /// Without it the tenant's webhooks are ignored.
    /// </summary>
    public string? WebhookAppSecretName { get; set; }

    /// <summary>Name of the secret holding the verify token that the webhook handshake must present.</summary>
    public string? WebhookVerifyTokenName { get; set; }
}

internal sealed class WhatsappTenantsOptionsValidator : IValidateOptions<WhatsappTenantsOptions>
{
    public ValidateOptionsResult Validate(string? name, WhatsappTenantsOptions options)
    {
        var failures = new List<string>();
        var phoneNumberOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tenantId, settings) in options.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                failures.Add("A tenant id must not be empty.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(settings.DefaultTemplateLanguage))
            {
                failures.Add($"Tenant '{tenantId}': DefaultTemplateLanguage is required.");
            }

            if (!settings.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(settings.PhoneNumberId))
            {
                failures.Add($"Tenant '{tenantId}': PhoneNumberId is required.");
            }
            else if (!phoneNumberOwners.TryAdd(settings.PhoneNumberId, tenantId))
            {
                // Webhooks name only the phone number, so two tenants sharing one would be indistinguishable.
                failures.Add($"Tenant '{tenantId}': PhoneNumberId is already used by tenant '{phoneNumberOwners[settings.PhoneNumberId]}'.");
            }

            if (string.IsNullOrWhiteSpace(settings.AccessTokenSecret))
            {
                failures.Add($"Tenant '{tenantId}': AccessTokenSecret is required.");
            }

            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUrl)
                || baseUrl.Scheme is not ("http" or "https"))
            {
                failures.Add($"Tenant '{tenantId}': BaseUrl must be an absolute http(s) URL.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>Resolves tenants from configuration. Every call returns a fresh immutable options object.</summary>
internal sealed class ConfigTenantOptionsResolver(
    IOptionsMonitor<WhatsappTenantsOptions> options,
    ISecretProvider secrets) : ITenantOptionsResolver
{
    public async ValueTask<WhatsappTenantOptions> ResolveAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!options.CurrentValue.Tenants.TryGetValue(tenantId, out var settings))
        {
            throw new TenantNotConfiguredException(tenantId);
        }

        if (!settings.Enabled)
        {
            return new WhatsappTenantOptions
            {
                TenantId = tenantId,
                Enabled = false,
                PhoneNumberId = settings.PhoneNumberId ?? string.Empty,
                AccessToken = string.Empty,
                BaseUrl = settings.BaseUrl ?? string.Empty,
                DefaultTemplateLanguage = settings.DefaultTemplateLanguage,
                ServiceWindowEnabled = settings.ServiceWindowEnabled,
            };
        }

        var token = await secrets.GetSecretAsync(settings.AccessTokenSecret!, cancellationToken).ConfigureAwait(false);
        return new WhatsappTenantOptions
        {
            TenantId = tenantId,
            Enabled = true,
            PhoneNumberId = settings.PhoneNumberId!,
            AccessToken = token,
            BaseUrl = settings.BaseUrl!.TrimEnd('/'),
            DefaultTemplateLanguage = settings.DefaultTemplateLanguage,
            ServiceWindowEnabled = settings.ServiceWindowEnabled,
        };
    }
}

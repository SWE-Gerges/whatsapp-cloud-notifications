using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>Finds webhook tenants in configuration. Secrets are read through <see cref="ISecretProvider"/> on every call.</summary>
internal sealed class ConfigWebhookTenantDirectory(
    IOptionsMonitor<WhatsappTenantsOptions> options,
    ISecretProvider secrets) : IWebhookTenantDirectory
{
    public async ValueTask<WebhookTenant?> FindByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumberId);

        foreach (var (tenantId, settings) in options.CurrentValue.Tenants)
        {
            if (string.Equals(settings.PhoneNumberId, phoneNumberId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(settings.WebhookAppSecretName))
            {
                var appSecret = await secrets.GetSecretAsync(settings.WebhookAppSecretName, cancellationToken).ConfigureAwait(false);
                return new WebhookTenant(tenantId, appSecret);
            }
        }

        return null;
    }

    public async ValueTask<bool> IsValidVerifyTokenAsync(string presentedToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentedToken);

        var presented = Hash(presentedToken);
        var matched = false;

        // No early exit: the time taken does not depend on which tenant, if any, owns the token.
        foreach (var settings in options.CurrentValue.Tenants.Values)
        {
            if (string.IsNullOrWhiteSpace(settings.WebhookVerifyTokenName))
            {
                continue;
            }

            var configured = await secrets.GetSecretAsync(settings.WebhookVerifyTokenName, cancellationToken).ConfigureAwait(false);
            matched |= CryptographicOperations.FixedTimeEquals(presented, Hash(configured));
        }

        return matched;
    }

    // Hashing first compares equal-length digests, so the length of a token does not leak either.
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

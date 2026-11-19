namespace Whatsapp.Abstractions;

/// <summary>Resolves the immutable options for a tenant. The tenant is always passed explicitly.</summary>
public interface ITenantOptionsResolver
{
    /// <exception cref="TenantNotConfiguredException">No configuration exists for the tenant.</exception>
    /// <exception cref="SecretNotFoundException">The tenant is enabled but its secret cannot be read.</exception>
    ValueTask<WhatsappTenantOptions> ResolveAsync(string tenantId, CancellationToken cancellationToken);
}

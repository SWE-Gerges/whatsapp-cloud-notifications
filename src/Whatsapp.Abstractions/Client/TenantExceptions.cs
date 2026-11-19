namespace Whatsapp.Abstractions;

public sealed class TenantNotConfiguredException(string tenantId)
    : InvalidOperationException($"Tenant '{tenantId}' is not configured.")
{
    public string TenantId { get; } = tenantId;
}

public sealed class SecretNotFoundException(string secretName)
    : InvalidOperationException($"Secret '{secretName}' was not found.")
{
    public string SecretName { get; } = secretName;
}

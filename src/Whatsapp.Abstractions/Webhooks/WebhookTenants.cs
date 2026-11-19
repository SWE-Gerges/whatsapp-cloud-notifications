namespace Whatsapp.Abstractions;

/// <summary>The tenant a webhook delivery belongs to, with the secret its signature must match.</summary>
public sealed record WebhookTenant(string TenantId, string AppSecret)
{
    // The generated ToString would print the secret.
    public override string ToString() => $"WebhookTenant {{ TenantId = {TenantId}, AppSecret = *** }}";
}

/// <summary>
/// Webhook side of the tenant configuration. It is separate from <see cref="ITenantOptionsResolver"/> because a
/// delivery arrives knowing only a phone number id, and because checking a verify token must not depend on
/// being able to resolve every tenant's access token.
/// </summary>
public interface IWebhookTenantDirectory
{
    /// <summary>The tenant that owns the phone number, or null when no configured tenant does (or it has no app secret).</summary>
    ValueTask<WebhookTenant?> FindByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the token matches the verify token of any configured tenant. Every configured token is always
    /// compared, in constant time, so neither the number of tenants nor the position of a match shows in the timing.
    /// </summary>
    ValueTask<bool> IsValidVerifyTokenAsync(string presentedToken, CancellationToken cancellationToken);
}

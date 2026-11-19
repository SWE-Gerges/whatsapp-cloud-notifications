namespace Whatsapp.Abstractions;

/// <summary>Source of secrets such as access tokens (environment, vault, ...).</summary>
public interface ISecretProvider
{
    /// <exception cref="SecretNotFoundException">No secret with that name exists.</exception>
    ValueTask<string> GetSecretAsync(string name, CancellationToken cancellationToken);
}

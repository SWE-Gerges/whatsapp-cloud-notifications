using Microsoft.Extensions.Configuration;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>
/// Reads secrets from the <c>Secrets</c> configuration section, which in practice is fed by environment
/// variables or a user-secrets file and is never committed.
/// </summary>
internal sealed class ConfigurationSecretProvider(IConfiguration configuration) : ISecretProvider
{
    public const string SectionName = "Secrets";

    public ValueTask<string> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var value = name.Contains(':', StringComparison.Ordinal)
            ? null
            : configuration[$"{SectionName}:{name}"];

        return string.IsNullOrWhiteSpace(value)
            ? ValueTask.FromException<string>(new SecretNotFoundException(name))
            : ValueTask.FromResult(value);
    }
}

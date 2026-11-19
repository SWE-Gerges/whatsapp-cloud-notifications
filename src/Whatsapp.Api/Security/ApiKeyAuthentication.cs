using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Whatsapp.CloudApi;

namespace Whatsapp.Api.Security;

public sealed class ApiAuthOptions
{
    public const string SectionName = "Api";

    /// <summary>Key for the admin routes. It never maps to a tenant.</summary>
    public string AdminKey { get; set; } = string.Empty;

    /// <summary>One key per tenant. The key identifies the tenant: callers never send a tenant id themselves.</summary>
    public List<TenantApiKey> TenantKeys { get; set; } = [];
}

public sealed class TenantApiKey
{
    public string TenantId { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;
}

internal sealed class ApiAuthOptionsValidator(IOptions<WhatsappTenantsOptions> tenants) : IValidateOptions<ApiAuthOptions>
{
    public const int MinimumKeyLength = 16;

    public ValidateOptionsResult Validate(string? name, ApiAuthOptions options)
    {
        var failures = new List<string>();
        var configuredTenants = ConfiguredTenants();

        if (options.AdminKey.Length < MinimumKeyLength)
        {
            failures.Add($"Api:AdminKey must be at least {MinimumKeyLength} characters long.");
        }

        var seenTenants = new HashSet<string>(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal) { options.AdminKey };
        for (var i = 0; i < options.TenantKeys.Count; i++)
        {
            var entry = options.TenantKeys[i];
            if (string.IsNullOrWhiteSpace(entry.TenantId))
            {
                failures.Add($"Api:TenantKeys:{i}:TenantId is required.");
            }
            else if (!seenTenants.Add(entry.TenantId))
            {
                failures.Add($"Api:TenantKeys has more than one key for tenant '{entry.TenantId}'.");
            }
            else if (configuredTenants is not null && !configuredTenants.ContainsKey(entry.TenantId))
            {
                failures.Add($"Api:TenantKeys:{i}:TenantId '{entry.TenantId}' has no entry under Whatsapp:Tenants.");
            }

            if (entry.Key.Length < MinimumKeyLength)
            {
                failures.Add($"Api:TenantKeys:{i}:Key must be at least {MinimumKeyLength} characters long.");
            }
            else if (!seenKeys.Add(entry.Key))
            {
                failures.Add($"Api:TenantKeys:{i}:Key must be unique and different from the admin key.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // When the tenant settings themselves are invalid they report their own failure; the cross-check is skipped
    // so that one misconfiguration produces one clear error instead of two.
    private Dictionary<string, WhatsappTenantSettings>? ConfiguredTenants()
    {
        try
        {
            return tenants.Value.Tenants;
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }
}

public static class ApiRoles
{
    public const string Admin = "admin";

    public const string Tenant = "tenant";
}

public static class ApiPolicies
{
    public const string AnyKey = "AnyKey";

    public const string TenantKey = "TenantKey";

    public const string AdminKey = "AdminKey";
}

public static class ApiPrincipalExtensions
{
    public const string TenantClaim = "tenant_id";

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.IsInRole(ApiRoles.Admin);

    public static string? GetTenantId(this ClaimsPrincipal principal) => principal.FindFirstValue(TenantClaim);
}

/// <summary>
/// Maps a presented key to a principal. Keys are hashed first, so every comparison works on equal-length digests,
/// and all configured keys are always compared, so neither the length nor the position of a key leaks through timing.
/// </summary>
internal sealed class ApiKeyStore
{
    private readonly byte[] _adminHash;
    private readonly (string TenantId, byte[] Hash)[] _tenants;

    public ApiKeyStore(IOptions<ApiAuthOptions> options)
    {
        var settings = options.Value;
        _adminHash = Hash(settings.AdminKey);
        _tenants = [.. settings.TenantKeys.Select(k => (k.TenantId, Hash(k.Key)))];
    }

    public ApiKeyMatch? Authenticate(string presentedKey)
    {
        var presented = Hash(presentedKey);

        var isAdmin = CryptographicOperations.FixedTimeEquals(presented, _adminHash);
        string? tenantId = null;
        foreach (var (candidateTenant, hash) in _tenants)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, hash))
            {
                tenantId = candidateTenant;
            }
        }

        if (isAdmin)
        {
            return new ApiKeyMatch(ApiRoles.Admin, null);
        }

        return tenantId is null ? null : new ApiKeyMatch(ApiRoles.Tenant, tenantId);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

internal sealed record ApiKeyMatch(string Role, string? TenantId);

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyStore keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header) || header.Count != 1 || string.IsNullOrEmpty(header[0]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var match = keys.Authenticate(header[0]!);
        if (match is null)
        {
            Logger.LogWarning("A request was rejected because of an unknown API key");
            return Task.FromResult(AuthenticateResult.Fail("The API key is not valid."));
        }

        var claims = new List<Claim> { new(ClaimTypes.Role, match.Role) };
        if (match.TenantId is not null)
        {
            claims.Add(new Claim(ApiPrincipalExtensions.TenantClaim, match.TenantId));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

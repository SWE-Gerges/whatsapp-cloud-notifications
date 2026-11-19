using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

public static class CloudApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Cloud API client, the E.164 normaliser and the configuration backed tenant resolver.
    /// Options are validated when the host starts.
    /// </summary>
    public static IServiceCollection AddWhatsappCloudApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration);
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<WhatsappClientOptions>()
            .Bind(configuration.GetSection(WhatsappClientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<WhatsappTenantsOptions>()
            .Bind(configuration.GetSection(WhatsappTenantsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<WhatsappTenantsOptions>, WhatsappTenantsOptionsValidator>());

        services.TryAddSingleton<ISecretProvider, ConfigurationSecretProvider>();
        services.TryAddSingleton<ITenantOptionsResolver, ConfigTenantOptionsResolver>();
        services.TryAddSingleton<IWebhookTenantDirectory, ConfigWebhookTenantDirectory>();
        services.TryAddSingleton<IPhoneNumberNormalizer, PhoneNumberNormalizer>();

        // The client applies its own per-attempt timeout, so the HttpClient timeout is disabled.
        services.AddHttpClient<IWhatsappClient, WhatsappCloudClient>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);

        return services;
    }
}

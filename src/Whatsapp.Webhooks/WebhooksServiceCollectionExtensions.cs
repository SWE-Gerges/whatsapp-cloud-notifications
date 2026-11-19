using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whatsapp.Webhooks;

public sealed class WebhookOptions
{
    public const string SectionName = "Whatsapp:Webhooks";

    /// <summary>Largest request body accepted. A larger one is answered with 413 before anything is read.</summary>
    [Range(1024, 16 * 1024 * 1024)]
    public int MaxBodyBytes { get; set; } = 1024 * 1024;
}

public static class WebhooksServiceCollectionExtensions
{
    /// <summary>
    /// Registers the webhook processor and its metrics. It needs the tenant directory, the phone normaliser and the
    /// two recorders, which the Cloud API and outbox modules provide.
    /// </summary>
    public static IServiceCollection AddWhatsappWebhooks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<WebhookMetrics>();
        services.TryAddSingleton<WhatsappWebhookProcessor>();
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Whatsapp.Events;

public static class EventsServiceCollectionExtensions
{
    /// <summary>
    /// Registers MediatR and the booking event handlers. This is the only place that calls <c>AddMediatR</c>:
    /// scanning the same assembly twice registers every handler twice, and every message would be queued twice.
    /// Calling this method more than once is harmless.
    /// </summary>
    public static IServiceCollection AddWhatsappEvents(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(EventsRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<EventsRegistrationMarker>();

        var options = services.AddOptions<EventDeliveryOptions>();
        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(EventDeliveryOptions.SectionName));
        }

        services.AddMediatR(config => config.RegisterServicesFromAssembly(typeof(BookingCreatedHandler).Assembly));
        return services;
    }

    private sealed class EventsRegistrationMarker;
}

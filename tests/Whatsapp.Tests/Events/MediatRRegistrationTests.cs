using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Whatsapp.Events;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Events;

public sealed class MediatRRegistrationTests
{
    private static readonly Type[] HandlerTypes = typeof(BookingCreatedHandler).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false }
            && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotificationHandler<>)))
        .ToArray();

    [Fact]
    public async Task Every_notification_handler_is_registered_exactly_once_in_the_real_container()
    {
        await using var host = new EventsTestHost();

        HandlerTypes.ShouldNotBeEmpty();
        foreach (var handlerType in HandlerTypes)
        {
            var registrations = host.Services
                .Where(d => d.ServiceType.IsGenericType
                    && d.ServiceType.GetGenericTypeDefinition() == typeof(INotificationHandler<>)
                    && (d.ImplementationType ?? d.ImplementationInstance?.GetType()) == handlerType)
                .ToList();

            registrations.Count.ShouldBe(1, $"{handlerType.Name} must be registered once");
        }
    }

    [Fact]
    public async Task Resolving_the_handlers_yields_one_instance_per_notification_type()
    {
        await using var host = new EventsTestHost();

        host.Provider.GetServices<INotificationHandler<BookingCreatedNotification>>()
            .Select(h => h.GetType()).ShouldBe([typeof(BookingCreatedHandler)]);
        host.Provider.GetServices<INotificationHandler<BookingStatusChangedNotification>>()
            .Select(h => h.GetType()).ShouldBe([typeof(BookingStatusChangedHandler)]);
    }

    [Fact]
    public async Task Calling_the_registration_method_again_does_not_register_handlers_a_second_time()
    {
        await using var host = new EventsTestHost(configureServices: services =>
        {
            services.AddWhatsappEvents();
            services.AddWhatsappEvents();
        });

        host.Provider.GetServices<INotificationHandler<BookingCreatedNotification>>().Count().ShouldBe(1);
        host.Provider.GetServices<INotificationHandler<BookingStatusChangedNotification>>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_published_event_reaches_each_handler_once()
    {
        await using var host = new EventsTestHost();

        await host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel()));

        host.Database.Rows().ShouldHaveSingleItem();
    }

    [Fact]
    public void Mediator_itself_is_registered_once()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWhatsappEvents();

        services.Count(d => d.ServiceType == typeof(IMediator)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IPublisher)).ShouldBe(1);
    }
}

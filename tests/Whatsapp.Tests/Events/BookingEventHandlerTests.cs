using Microsoft.Extensions.DependencyInjection;
using Whatsapp.Abstractions;
using Whatsapp.Abstractions.Bookings;
using Whatsapp.Events;
using Whatsapp.Outbox;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Events;

public sealed class BookingEventHandlerTests : IAsyncDisposable
{
    private readonly EventsTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static INotificationEnqueuer FailingEnqueuer()
    {
        var enqueuer = Substitute.For<INotificationEnqueuer>();
        enqueuer.EnqueueAsync(default!, default)
            .ReturnsForAnyArgs(_ => Task.FromException<EnqueueResult>(new InvalidOperationException("db down")));
        return enqueuer;
    }

    [Fact]
    public async Task Booking_created_is_queued_as_a_template_and_nothing_is_sent()
    {
        await _host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel()));

        var row = _host.Database.Rows().ShouldHaveSingleItem();
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.Kind.ShouldBe(NotificationKind.Template);
        row.TenantId.ShouldBe("tenant-a");
        row.Recipient.ShouldBe("+12025550100");
        row.EventKey.ShouldBe("booking-created");
        row.ReferenceKey.ShouldBe("bk-1001");
        row.TemplateName.ShouldBe("booking_created");
        row.TemplateLanguage.ShouldBe("en");
        row.TemplateParameters.ShouldBe(["Sam", "Acme Travel", "Hotels", "2026-03-01", "Check-in 2026-03-14"]);
        _host.Http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Booking_status_changed_is_queued_as_a_template_and_nothing_is_sent()
    {
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(BookingStatus.Cancelled)));

        var row = _host.Database.Rows().ShouldHaveSingleItem();
        row.EventKey.ShouldBe("booking-status-changed");
        row.ReferenceKey.ShouldBe("bk-1001:Cancelled");
        row.TemplateName.ShouldBe("booking_status_updated");
        row.TemplateParameters.ShouldBe(["Sam", "REF-1001", "Cancelled"]);
        _host.Http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Processing_the_queue_is_what_makes_the_http_call()
    {
        _host.Http.Respond(System.Net.HttpStatusCode.OK, TestData.SuccessBody);
        await _host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel()));
        _host.Http.Requests.ShouldBeEmpty();

        using var scope = _host.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _host.Http.Requests.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/v1/100200300/messages");
        _host.Database.Rows().ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task The_same_event_published_twice_queues_one_notification()
    {
        await _host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel()));
        await _host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel()));
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(BookingStatus.Confirmed)));
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(BookingStatus.Confirmed)));

        _host.Database.Rows().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Each_new_status_of_a_booking_is_queued_separately()
    {
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(BookingStatus.Confirmed)));
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(BookingStatus.Cancelled)));

        _host.Database.Rows().Select(r => r.ReferenceKey).ShouldBe(["bk-1001:Confirmed", "bk-1001:Cancelled"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_status_change_is_skipped_when_the_customer_did_not_opt_in()
    {
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(optedIn: false)));

        _host.Database.Rows().ShouldBeEmpty();
    }

    [Fact]
    public async Task Booking_created_does_not_depend_on_the_opt_in_flag()
    {
        await _host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel(optedIn: false)));

        _host.Database.Rows().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_invalid_phone_number_queues_nothing_and_does_not_break_the_publisher()
    {
        await _host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel(national: "123")));
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(callingCode: "999")));

        _host.Database.Rows().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_customer_language_selects_the_template_language()
    {
        await _host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(BookingStatus.Cancelled, language: "ar")));

        var row = _host.Database.Rows().ShouldHaveSingleItem();
        row.TemplateLanguage.ShouldBe("ar");
        row.TemplateParameters[2].ShouldBe("ملغي");
    }

    [Fact]
    public async Task National_phone_formats_are_normalised_to_e164()
    {
        await _host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel(callingCode: "1", national: "(212) 555-0143")));

        _host.Database.Rows().ShouldHaveSingleItem().Recipient.ShouldBe("+12125550143");
    }

    [Fact]
    public async Task A_database_failure_is_not_swallowed_so_the_publisher_can_retry()
    {
        await using var host = new EventsTestHost(configureServices: services => services.AddSingleton(FailingEnqueuer()));

        await Should.ThrowAsync<InvalidOperationException>(
            () => host.PublishAsync(new BookingCreatedNotification(Snapshots.Hotel())));
    }

    [Fact]
    public async Task Free_text_can_be_preferred_with_the_template_as_fallback()
    {
        await using var host = new EventsTestHost(new Dictionary<string, string?> { ["Whatsapp:Events:PreferFreeTextWithinWindow"] = "true" });

        await host.PublishAsync(new BookingStatusChangedNotification(Snapshots.Hotel(BookingStatus.Cancelled)));

        var row = host.Database.Rows().ShouldHaveSingleItem();
        row.Kind.ShouldBe(NotificationKind.FreeText);
        row.Text.ShouldBe("Hi Sam, the status of your booking REF-1001 is now: Cancelled.");
        row.TemplateName.ShouldBe("booking_status_updated");
        row.TemplateParameters.ShouldBe(["Sam", "REF-1001", "Cancelled"]);
    }
}

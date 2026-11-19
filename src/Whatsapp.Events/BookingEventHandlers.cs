using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;
using Whatsapp.Abstractions.Bookings;
using Whatsapp.Templates;

namespace Whatsapp.Events;

/// <summary>
/// Handlers only describe the message and put it in the outbox. They never call the Cloud API, so a problem on
/// the WhatsApp side cannot affect the booking flow that raised the event.
/// </summary>
internal static class EventNotificationFactory
{
    public static EnqueueNotification Create(
        BookingSnapshot snapshot,
        string recipient,
        string eventKey,
        string referenceKey,
        TemplateMessageSpec template,
        string freeText,
        bool preferFreeText) =>
        preferFreeText
            ? new EnqueueNotification(
                snapshot.TenantId,
                recipient,
                eventKey,
                referenceKey,
                NotificationKind.FreeText,
                template.TemplateName,
                template.LanguageCode,
                template.Parameters,
                freeText)
            : new EnqueueNotification(
                snapshot.TenantId,
                recipient,
                eventKey,
                referenceKey,
                NotificationKind.Template,
                template.TemplateName,
                template.LanguageCode,
                template.Parameters);
}

public sealed class BookingCreatedHandler(
    ITemplateBuilder templates,
    IBookingFreeTextComposer freeText,
    IPhoneNumberNormalizer phones,
    INotificationEnqueuer enqueuer,
    IOptions<EventDeliveryOptions> options,
    ILogger<BookingCreatedHandler> logger) : INotificationHandler<BookingCreatedNotification>
{
    public async Task Handle(BookingCreatedNotification notification, CancellationToken cancellationToken)
    {
        var snapshot = notification.Snapshot;

        var phone = phones.Normalize(snapshot.PhoneCountryCallingCode, snapshot.PhoneNationalNumber);
        if (!phone.IsValid)
        {
            logger.LogWarning(
                "Booking {BookingId} (tenant {TenantId}) has an invalid phone number, no notification was queued: {Reason}",
                snapshot.BookingId,
                snapshot.TenantId,
                phone.FailureReason);
            return;
        }

        var preferFreeText = options.Value.PreferFreeTextWithinWindow;
        var request = EventNotificationFactory.Create(
            snapshot,
            phone.E164!,
            EventKeys.BookingCreated,
            EventKeys.ReferenceForCreated(snapshot),
            templates.BuildBookingCreated(snapshot),
            preferFreeText ? freeText.ComposeBookingCreated(snapshot) : string.Empty,
            preferFreeText);

        await enqueuer.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class BookingStatusChangedHandler(
    ITemplateBuilder templates,
    IBookingFreeTextComposer freeText,
    IPhoneNumberNormalizer phones,
    INotificationEnqueuer enqueuer,
    IOptions<EventDeliveryOptions> options,
    ILogger<BookingStatusChangedHandler> logger) : INotificationHandler<BookingStatusChangedNotification>
{
    public async Task Handle(BookingStatusChangedNotification notification, CancellationToken cancellationToken)
    {
        var snapshot = notification.Snapshot;

        if (!snapshot.WhatsappUpdatesEnabled)
        {
            logger.LogDebug(
                "Booking {BookingId} (tenant {TenantId}): the customer did not opt in to status updates",
                snapshot.BookingId,
                snapshot.TenantId);
            return;
        }

        var phone = phones.Normalize(snapshot.PhoneCountryCallingCode, snapshot.PhoneNationalNumber);
        if (!phone.IsValid)
        {
            logger.LogWarning(
                "Booking {BookingId} (tenant {TenantId}) has an invalid phone number, no notification was queued: {Reason}",
                snapshot.BookingId,
                snapshot.TenantId,
                phone.FailureReason);
            return;
        }

        var preferFreeText = options.Value.PreferFreeTextWithinWindow;
        var request = EventNotificationFactory.Create(
            snapshot,
            phone.E164!,
            EventKeys.BookingStatusChanged,
            EventKeys.ReferenceForStatusChanged(snapshot),
            templates.BuildBookingStatusUpdated(snapshot),
            preferFreeText ? freeText.ComposeBookingStatusUpdated(snapshot) : string.Empty,
            preferFreeText);

        await enqueuer.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

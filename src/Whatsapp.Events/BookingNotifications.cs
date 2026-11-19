using MediatR;
using Whatsapp.Abstractions.Bookings;

namespace Whatsapp.Events;

/// <summary>A booking was created. Carries an immutable snapshot of the booking.</summary>
public sealed record BookingCreatedNotification(BookingSnapshot Snapshot) : INotification;

/// <summary>The status of a booking changed. Carries an immutable snapshot with the new status.</summary>
public sealed record BookingStatusChangedNotification(BookingSnapshot Snapshot) : INotification;

/// <summary>
/// Deterministic outbox keys for booking events. The same booking event always maps to the same key,
/// which is what makes a re-posted event a duplicate instead of a second message.
/// </summary>
public static class EventKeys
{
    public const string BookingCreated = "booking-created";

    public const string BookingStatusChanged = "booking-status-changed";

    public static string ReferenceForCreated(BookingSnapshot snapshot) => snapshot.BookingId.Trim();

    /// <summary>Each (booking, status) pair is notified once; a later return to an earlier status is not re-sent.</summary>
    public static string ReferenceForStatusChanged(BookingSnapshot snapshot) =>
        $"{snapshot.BookingId.Trim()}:{snapshot.Status}";
}

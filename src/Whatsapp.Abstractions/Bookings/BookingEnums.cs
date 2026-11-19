namespace Whatsapp.Abstractions.Bookings;

public enum BookingType
{
    Hotel = 0,
    Flight = 1,
    Transfer = 2,
    Activity = 3,
}

public enum BookingStatus
{
    Confirmed = 0,
    TicketedOrVouchered = 1,
    PaymentCompleted = 2,
    ConfirmedPendingPayment = 3,
    CancellationPending = 4,
    Cancelled = 5,
    RefundProcessing = 6,
    RefundCompleted = 7,
    Updated = 8,
}

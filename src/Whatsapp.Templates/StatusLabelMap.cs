using Whatsapp.Abstractions.Bookings;

namespace Whatsapp.Templates;

/// <summary>Maps internal booking statuses to the wording shown to customers. Replace it to change the wording.</summary>
public interface IStatusLabelMap
{
    string GetLabel(BookingStatus status, string? languageCode = null);
}

public sealed class DefaultStatusLabelMap : IStatusLabelMap
{
    private static readonly IReadOnlyDictionary<BookingStatus, string> English = new Dictionary<BookingStatus, string>
    {
        [BookingStatus.Confirmed] = "Confirmed",
        [BookingStatus.TicketedOrVouchered] = "Ticketed/Vouchered",
        [BookingStatus.PaymentCompleted] = "Payment Completed",
        [BookingStatus.ConfirmedPendingPayment] = "Confirmed - Pending Payment",
        [BookingStatus.CancellationPending] = "Cancellation Pending",
        [BookingStatus.Cancelled] = "Cancelled",
        [BookingStatus.RefundProcessing] = "Refund Processing",
        [BookingStatus.RefundCompleted] = "Refund Completed",
        [BookingStatus.Updated] = "Updated",
    };

    private static readonly IReadOnlyDictionary<BookingStatus, string> Arabic = new Dictionary<BookingStatus, string>
    {
        [BookingStatus.Confirmed] = "مؤكد",
        [BookingStatus.TicketedOrVouchered] = "تم إصدار التذكرة/القسيمة",
        [BookingStatus.PaymentCompleted] = "تم الدفع",
        [BookingStatus.ConfirmedPendingPayment] = "مؤكد - في انتظار الدفع",
        [BookingStatus.CancellationPending] = "الإلغاء قيد المعالجة",
        [BookingStatus.Cancelled] = "ملغي",
        [BookingStatus.RefundProcessing] = "الاسترداد قيد المعالجة",
        [BookingStatus.RefundCompleted] = "تم الاسترداد",
        [BookingStatus.Updated] = "تم التحديث",
    };

    public string GetLabel(BookingStatus status, string? languageCode = null)
    {
        var table = string.Equals(
            string.IsNullOrWhiteSpace(languageCode) ? null : LanguageCodes.Primary(languageCode),
            "ar",
            StringComparison.OrdinalIgnoreCase)
            ? Arabic
            : English;

        return table.TryGetValue(status, out var label)
            ? label
            : throw new ArgumentOutOfRangeException(nameof(status), status, "The booking status has no label.");
    }
}

using Whatsapp.Abstractions.Bookings;

namespace Whatsapp.Tests.Support;

internal static class Snapshots
{
    public static BookingSnapshot Hotel(
        BookingStatus status = BookingStatus.Confirmed,
        string tenantId = "tenant-a",
        string bookingId = "bk-1001",
        string? language = "en",
        bool optedIn = true,
        string callingCode = "1",
        string national = "2025550100") => new()
    {
        TenantId = tenantId,
        BookingId = bookingId,
        Reference = "REF-1001",
        CustomerFirstName = "Sam",
        PhoneCountryCallingCode = callingCode,
        PhoneNationalNumber = national,
        BookingType = BookingType.Hotel,
        Status = status,
        Details = new BookingDetailsInput { CheckInDate = new DateOnly(2026, 3, 14) },
        CompanyName = "Acme Travel",
        CreatedAt = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero),
        PreferredLanguage = language,
        WhatsappUpdatesEnabled = optedIn,
    };
}

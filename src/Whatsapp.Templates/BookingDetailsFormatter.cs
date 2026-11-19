using System.Globalization;
using Whatsapp.Abstractions.Bookings;

namespace Whatsapp.Templates;

/// <summary>Builds the short, customer facing detail line of a booking from its snapshot.</summary>
public static class BookingDetailsFormatter
{
    private sealed record Words(
        string Hotel,
        string Flight,
        string Transfer,
        string Activity,
        string At,
        string HotelFallback,
        string FlightFallback,
        string TransferFallback,
        string ActivityFallback);

    private static readonly Words English = new(
        "Check-in",
        "Departure",
        "Pickup",
        "Activity date",
        "at",
        "Hotel booking",
        "Flight booking",
        "Transfer booking",
        "Activity booking");

    private static readonly Words Arabic = new(
        "تسجيل الوصول",
        "المغادرة",
        "الاستلام",
        "تاريخ النشاط",
        "في",
        "حجز فندق",
        "حجز طيران",
        "حجز انتقال",
        "حجز نشاط");

    public static string Format(BookingSnapshot snapshot, string? languageCode = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var words = IsArabic(languageCode) ? Arabic : English;
        var details = snapshot.Details;

        return snapshot.BookingType switch
        {
            BookingType.Hotel => details.CheckInDate is { } checkIn
                ? $"{words.Hotel} {FormatDate(checkIn)}"
                : words.HotelFallback,

            BookingType.Flight => FormatFlight(details, words),

            BookingType.Transfer => details.PickupAt is { } pickup
                ? $"{words.Transfer} {FormatDateTime(pickup)}"
                    + (string.IsNullOrWhiteSpace(details.PickupLocation) ? string.Empty : $" {words.At} {details.PickupLocation.Trim()}")
                : words.TransferFallback,

            BookingType.Activity => details.ActivityDate is { } date
                ? $"{words.Activity} {FormatDate(date)}"
                : words.ActivityFallback,

            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.BookingType, "Unknown booking type."),
        };
    }

    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string FormatDate(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string FormatDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string FormatFlight(BookingDetailsInput details, Words words)
    {
        if (details.DepartureAt is not { } departure)
        {
            return words.FlightFallback;
        }

        var text = $"{words.Flight} {FormatDateTime(departure)}";
        return !string.IsNullOrWhiteSpace(details.Origin) && !string.IsNullOrWhiteSpace(details.Destination)
            ? $"{text}, {details.Origin.Trim()} - {details.Destination.Trim()}"
            : text;
    }

    private static bool IsArabic(string? languageCode) =>
        !string.IsNullOrWhiteSpace(languageCode)
        && string.Equals(LanguageCodes.Primary(languageCode), "ar", StringComparison.OrdinalIgnoreCase);
}

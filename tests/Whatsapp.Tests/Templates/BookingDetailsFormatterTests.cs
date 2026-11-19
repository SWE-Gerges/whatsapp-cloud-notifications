using Whatsapp.Abstractions.Bookings;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Templates;

public class BookingDetailsFormatterTests
{
    private static BookingSnapshot With(BookingType type, BookingDetailsInput details) =>
        Snapshots.Hotel() with { BookingType = type, Details = details };

    [Fact]
    public void Hotel_shows_the_check_in_date()
    {
        BookingDetailsFormatter.Format(With(BookingType.Hotel, new BookingDetailsInput { CheckInDate = new DateOnly(2026, 7, 4) }))
            .ShouldBe("Check-in 2026-07-04");
    }

    [Fact]
    public void Flight_shows_departure_and_route()
    {
        var details = new BookingDetailsInput
        {
            DepartureAt = new DateTimeOffset(2026, 7, 4, 8, 30, 0, TimeSpan.Zero),
            Origin = "AAA",
            Destination = "BBB",
        };

        BookingDetailsFormatter.Format(With(BookingType.Flight, details))
            .ShouldBe("Departure 2026-07-04 08:30 UTC, AAA - BBB");
    }

    [Fact]
    public void Flight_departure_is_converted_to_utc()
    {
        var details = new BookingDetailsInput
        {
            DepartureAt = new DateTimeOffset(2026, 7, 4, 10, 30, 0, TimeSpan.FromHours(2)),
        };

        BookingDetailsFormatter.Format(With(BookingType.Flight, details))
            .ShouldBe("Departure 2026-07-04 08:30 UTC");
    }

    [Fact]
    public void Transfer_shows_pickup_time_and_location()
    {
        var details = new BookingDetailsInput
        {
            PickupAt = new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero),
            PickupLocation = "Main Terminal",
        };

        BookingDetailsFormatter.Format(With(BookingType.Transfer, details))
            .ShouldBe("Pickup 2026-07-04 09:00 UTC at Main Terminal");
    }

    [Fact]
    public void Activity_shows_its_date()
    {
        BookingDetailsFormatter.Format(With(BookingType.Activity, new BookingDetailsInput { ActivityDate = new DateOnly(2026, 7, 5) }))
            .ShouldBe("Activity date 2026-07-05");
    }

    [Theory]
    [InlineData(BookingType.Hotel, "Hotel booking")]
    [InlineData(BookingType.Flight, "Flight booking")]
    [InlineData(BookingType.Transfer, "Transfer booking")]
    [InlineData(BookingType.Activity, "Activity booking")]
    public void Missing_inputs_fall_back_to_a_generic_description(BookingType type, string expected)
    {
        BookingDetailsFormatter.Format(With(type, new BookingDetailsInput())).ShouldBe(expected);
    }

    [Fact]
    public void Arabic_uses_arabic_labels_with_the_same_numeric_dates()
    {
        var text = BookingDetailsFormatter.Format(
            With(BookingType.Hotel, new BookingDetailsInput { CheckInDate = new DateOnly(2026, 7, 4) }),
            "ar");

        text.ShouldBe("تسجيل الوصول 2026-07-04");
    }
}

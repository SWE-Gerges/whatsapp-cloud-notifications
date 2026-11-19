using Whatsapp.Abstractions.Bookings;
using Whatsapp.Templates;

namespace Whatsapp.Tests.Templates;

public class StatusLabelMapTests
{
    private readonly DefaultStatusLabelMap _map = new();

    [Theory]
    [InlineData(BookingStatus.Confirmed, "Confirmed")]
    [InlineData(BookingStatus.TicketedOrVouchered, "Ticketed/Vouchered")]
    [InlineData(BookingStatus.PaymentCompleted, "Payment Completed")]
    [InlineData(BookingStatus.ConfirmedPendingPayment, "Confirmed - Pending Payment")]
    [InlineData(BookingStatus.CancellationPending, "Cancellation Pending")]
    [InlineData(BookingStatus.Cancelled, "Cancelled")]
    [InlineData(BookingStatus.RefundProcessing, "Refund Processing")]
    [InlineData(BookingStatus.RefundCompleted, "Refund Completed")]
    [InlineData(BookingStatus.Updated, "Updated")]
    public void English_labels_match_the_customer_facing_wording(BookingStatus status, string label)
    {
        _map.GetLabel(status).ShouldBe(label);
        _map.GetLabel(status, "en").ShouldBe(label);
        _map.GetLabel(status, "fr").ShouldBe(label);
    }

    [Fact]
    public void Every_status_has_an_english_and_an_arabic_label()
    {
        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            _map.GetLabel(status, "en").ShouldNotBeNullOrWhiteSpace();
            _map.GetLabel(status, "ar").ShouldNotBeNullOrWhiteSpace();
            _map.GetLabel(status, "ar").ShouldNotBe(_map.GetLabel(status, "en"));
        }
    }

    [Fact]
    public void Arabic_is_selected_by_primary_language_subtag()
    {
        _map.GetLabel(BookingStatus.Cancelled, "ar-EG").ShouldBe(_map.GetLabel(BookingStatus.Cancelled, "ar"));
    }

    [Fact]
    public void An_unmapped_status_is_a_programming_error()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => _map.GetLabel((BookingStatus)999));
    }
}

namespace Whatsapp.Abstractions.Bookings;

/// <summary>Immutable view of a booking at the moment an event was raised.</summary>
public sealed record BookingSnapshot
{
    public required string TenantId { get; init; }

    public required string BookingId { get; init; }

    /// <summary>Customer facing booking reference.</summary>
    public required string Reference { get; init; }

    public required string CustomerFirstName { get; init; }

    /// <summary>Country calling code without the plus sign, for example <c>44</c>.</summary>
    public required string PhoneCountryCallingCode { get; init; }

    public required string PhoneNationalNumber { get; init; }

    public required BookingType BookingType { get; init; }

    public required BookingStatus Status { get; init; }

    public required BookingDetailsInput Details { get; init; }

    public required string CompanyName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Preferred language of the customer, for example <c>en</c> or <c>ar</c>.</summary>
    public string? PreferredLanguage { get; init; }

    /// <summary>Whether the customer opted in to status updates over WhatsApp.</summary>
    public bool WhatsappUpdatesEnabled { get; init; }
}

/// <summary>The inputs the booking detail text is built from. Which fields are used depends on the booking type.</summary>
public sealed record BookingDetailsInput
{
    /// <summary>Hotel: check-in date.</summary>
    public DateOnly? CheckInDate { get; init; }

    /// <summary>Flight: departure time.</summary>
    public DateTimeOffset? DepartureAt { get; init; }

    /// <summary>Flight: departure airport or city.</summary>
    public string? Origin { get; init; }

    /// <summary>Flight: arrival airport or city.</summary>
    public string? Destination { get; init; }

    /// <summary>Transfer: pickup time.</summary>
    public DateTimeOffset? PickupAt { get; init; }

    /// <summary>Transfer: pickup location.</summary>
    public string? PickupLocation { get; init; }

    /// <summary>Activity: date of the activity.</summary>
    public DateOnly? ActivityDate { get; init; }
}

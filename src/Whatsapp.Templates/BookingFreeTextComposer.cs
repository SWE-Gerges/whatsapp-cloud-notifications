using Whatsapp.Abstractions.Bookings;

namespace Whatsapp.Templates;

/// <summary>Composes the free-text (non-template) wording of booking events, in the customer's language.</summary>
public interface IBookingFreeTextComposer
{
    string ComposeBookingCreated(BookingSnapshot snapshot);

    string ComposeBookingStatusUpdated(BookingSnapshot snapshot);
}

public sealed class BookingFreeTextComposer(FreeTextRenderer renderer, IStatusLabelMap statusLabels) : IBookingFreeTextComposer
{
    private static readonly IReadOnlyDictionary<string, string> CreatedVariants = new Dictionary<string, string>
    {
        ["en"] = "Hi {customer_name}, your {module} booking with {company_name} has been received. {details}. Created on {created_date}.",
        ["ar"] = "مرحباً {customer_name}، تم استلام حجزك ({module}) لدى {company_name}. {details}. تاريخ الإنشاء {created_date}.",
    };

    private static readonly IReadOnlyDictionary<string, string> StatusVariants = new Dictionary<string, string>
    {
        ["en"] = "Hi {customer_name}, the status of your booking {reference} is now: {status}.",
        ["ar"] = "مرحباً {customer_name}، حالة حجزك {reference} الآن: {status}.",
    };

    public string ComposeBookingCreated(BookingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return renderer.Render(
            CreatedVariants,
            new Dictionary<string, string>
            {
                ["customer_name"] = Clean(snapshot.CustomerFirstName, 60),
                ["company_name"] = Clean(snapshot.CompanyName, 60),
                ["module"] = TemplateBuilder.ModuleLabel(snapshot.BookingType, snapshot.PreferredLanguage),
                ["details"] = Clean(BookingDetailsFormatter.Format(snapshot, snapshot.PreferredLanguage), 200),
                ["created_date"] = BookingDetailsFormatter.FormatDate(snapshot.CreatedAt),
            },
            snapshot.PreferredLanguage);
    }

    public string ComposeBookingStatusUpdated(BookingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return renderer.Render(
            StatusVariants,
            new Dictionary<string, string>
            {
                ["customer_name"] = Clean(snapshot.CustomerFirstName, 60),
                ["reference"] = Clean(snapshot.Reference, 60),
                ["status"] = statusLabels.GetLabel(snapshot.Status, snapshot.PreferredLanguage),
            },
            snapshot.PreferredLanguage);
    }

    private static string Clean(string value, int maxLength) => TemplateText.Clean(value, maxLength);
}

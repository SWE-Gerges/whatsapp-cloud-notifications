using System.Text.RegularExpressions;
using Whatsapp.Abstractions.Bookings;

namespace Whatsapp.Templates;

/// <summary>A template send description: which template, in which language, with which parameters.</summary>
/// <param name="LanguageCode">
/// Language the customer prefers when the template supports it, otherwise null so the tenant default applies.
/// </param>
public sealed record TemplateMessageSpec(string TemplateName, string? LanguageCode, IReadOnlyList<string> Parameters);

/// <summary>Turns booking events into template send descriptions.</summary>
public interface ITemplateBuilder
{
    TemplateMessageSpec BuildBookingCreated(BookingSnapshot snapshot);

    TemplateMessageSpec BuildBookingStatusUpdated(BookingSnapshot snapshot);
}

public sealed class TemplateBuilder(ITemplateRegistry registry, IStatusLabelMap statusLabels) : ITemplateBuilder
{
    public TemplateMessageSpec BuildBookingCreated(BookingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var definition = registry.Get(TemplateNames.BookingCreated);
        var language = definition.ResolveLanguage(snapshot.PreferredLanguage);

        return Create(
            definition,
            language,
            snapshot.CustomerFirstName,
            snapshot.CompanyName,
            ModuleLabel(snapshot.BookingType, language),
            BookingDetailsFormatter.FormatDate(snapshot.CreatedAt),
            BookingDetailsFormatter.Format(snapshot, language));
    }

    public TemplateMessageSpec BuildBookingStatusUpdated(BookingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var definition = registry.Get(TemplateNames.BookingStatusUpdated);
        var language = definition.ResolveLanguage(snapshot.PreferredLanguage);

        return Create(
            definition,
            language,
            snapshot.CustomerFirstName,
            snapshot.Reference,
            statusLabels.GetLabel(snapshot.Status, language));
    }

    internal static string ModuleLabel(BookingType type, string? language)
    {
        var arabic = !string.IsNullOrWhiteSpace(language)
            && string.Equals(LanguageCodes.Primary(language), "ar", StringComparison.OrdinalIgnoreCase);

        return type switch
        {
            BookingType.Hotel => arabic ? "الفنادق" : "Hotels",
            BookingType.Flight => arabic ? "الطيران" : "Flights",
            BookingType.Transfer => arabic ? "الانتقالات" : "Transfers",
            BookingType.Activity => arabic ? "الأنشطة" : "Activities",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown booking type."),
        };
    }

    // Event data is free-form customer input. It is normalised so it always satisfies the provider rules
    // (no line breaks, no long space runs, bounded length) instead of failing the whole notification.
    private static TemplateMessageSpec Create(TemplateDefinition definition, string? language, params string[] values)
    {
        var parameters = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            parameters[i] = TemplateText.Clean(values[i], definition.Parameters[i].MaxLength);
        }

        return new TemplateMessageSpec(definition.Name, language, parameters);
    }
}

/// <summary>Normalisation of template parameter text.</summary>
public static partial class TemplateText
{
    public const string Placeholder = "-";

    /// <summary>
    /// Replaces any run of whitespace (including line breaks and tabs) by one space, trims, and shortens the
    /// text to <paramref name="maxLength"/> characters with an ellipsis. Empty text becomes a dash because the
    /// provider rejects empty parameters.
    /// </summary>
    public static string Clean(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 4);

        var collapsed = Whitespace().Replace(value ?? string.Empty, " ").Trim();
        if (collapsed.Length == 0)
        {
            return Placeholder;
        }

        return collapsed.Length <= maxLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, maxLength - 3).TrimEnd(), "...");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

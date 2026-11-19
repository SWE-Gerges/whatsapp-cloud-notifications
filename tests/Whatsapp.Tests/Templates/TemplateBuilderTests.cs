using Whatsapp.Abstractions.Bookings;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Templates;

public class TemplateBuilderTests
{
    private readonly TemplateBuilder _builder = new(new TemplateRegistry(DefaultTemplates.All), new DefaultStatusLabelMap());

    [Fact]
    public void Booking_created_builds_the_five_ordered_parameters()
    {
        var spec = _builder.BuildBookingCreated(Snapshots.Hotel());

        spec.TemplateName.ShouldBe("booking_created");
        spec.LanguageCode.ShouldBe("en");
        spec.Parameters.ShouldBe(["Sam", "Acme Travel", "Hotels", "2026-03-01", "Check-in 2026-03-14"]);
    }

    [Fact]
    public void Booking_status_updated_builds_name_reference_and_label()
    {
        var spec = _builder.BuildBookingStatusUpdated(Snapshots.Hotel(BookingStatus.ConfirmedPendingPayment));

        spec.TemplateName.ShouldBe("booking_status_updated");
        spec.Parameters.ShouldBe(["Sam", "REF-1001", "Confirmed - Pending Payment"]);
    }

    [Fact]
    public void Arabic_preference_selects_arabic_wording()
    {
        var spec = _builder.BuildBookingStatusUpdated(Snapshots.Hotel(BookingStatus.Cancelled, language: "ar"));

        spec.LanguageCode.ShouldBe("ar");
        spec.Parameters[2].ShouldBe("ملغي");
    }

    [Theory]
    [InlineData("ar-EG", "ar")]
    [InlineData("EN-gb", "en")]
    public void Regional_variants_map_to_the_supported_template_language(string preferred, string expected)
    {
        _builder.BuildBookingCreated(Snapshots.Hotel(language: preferred)).LanguageCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData(null)]
    [InlineData("")]
    public void Unsupported_language_leaves_the_language_open_for_the_tenant_default(string? preferred)
    {
        _builder.BuildBookingCreated(Snapshots.Hotel(language: preferred)).LanguageCode.ShouldBeNull();
    }

    [Fact]
    public void Free_form_customer_data_is_normalised_so_it_always_passes_validation()
    {
        var snapshot = Snapshots.Hotel() with
        {
            CustomerFirstName = "  Sam\nthe\ttraveller   ",
            CompanyName = "Acme     Travel\r\nLtd",
            Reference = new string('R', 200),
        };

        var created = _builder.BuildBookingCreated(snapshot);
        var status = _builder.BuildBookingStatusUpdated(snapshot);

        created.Parameters[0].ShouldBe("Sam the traveller");
        created.Parameters[1].ShouldBe("Acme Travel Ltd");
        status.Parameters[1].Length.ShouldBe(60);
        status.Parameters[1].ShouldEndWith("...");
        TemplateValidator.Validate(DefaultTemplates.BookingCreated, created.LanguageCode, created.Parameters).ShouldBeEmpty();
        TemplateValidator.Validate(DefaultTemplates.BookingStatusUpdated, status.LanguageCode, status.Parameters).ShouldBeEmpty();
    }

    [Fact]
    public void Empty_values_become_a_dash_because_the_provider_rejects_empty_parameters()
    {
        var spec = _builder.BuildBookingStatusUpdated(Snapshots.Hotel() with { CustomerFirstName = "  " });

        spec.Parameters[0].ShouldBe("-");
    }

    [Fact]
    public void Every_status_builds_a_valid_template_in_both_languages()
    {
        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            foreach (var language in new[] { "en", "ar" })
            {
                var spec = _builder.BuildBookingStatusUpdated(Snapshots.Hotel(status, language: language));
                TemplateValidator.Validate(DefaultTemplates.BookingStatusUpdated, spec.LanguageCode, spec.Parameters).ShouldBeEmpty();
            }
        }
    }

    [Fact]
    public void A_custom_label_map_replaces_the_wording()
    {
        var labels = NSubstitute.Substitute.For<IStatusLabelMap>();
        labels.GetLabel(BookingStatus.Confirmed, Arg.Any<string?>()).Returns("All good");
        var builder = new TemplateBuilder(new TemplateRegistry(DefaultTemplates.All), labels);

        builder.BuildBookingStatusUpdated(Snapshots.Hotel()).Parameters[2].ShouldBe("All good");
    }

    [Fact]
    public void Clean_rejects_a_limit_too_small_for_an_ellipsis()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => TemplateText.Clean("abc", 3));
    }

    [Fact]
    public void Clean_keeps_text_at_the_limit_untouched()
    {
        TemplateText.Clean(new string('a', 10), 10).ShouldBe(new string('a', 10));
        TemplateText.Clean(new string('a', 11), 10).ShouldBe("aaaaaaa...");
    }
}

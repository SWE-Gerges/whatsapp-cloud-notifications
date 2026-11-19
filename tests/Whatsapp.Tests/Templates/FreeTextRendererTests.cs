using Whatsapp.Abstractions.Bookings;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Templates;

public class FreeTextRendererTests
{
    private readonly FreeTextRenderer _renderer = new();

    private static readonly Dictionary<string, string> Variants = new()
    {
        ["en"] = "Hello {name}, your table is ready.",
        ["ar"] = "مرحباً {name}، طاولتك جاهزة.",
    };

    private static readonly Dictionary<string, string> Values = new() { ["name"] = "Sam" };

    [Theory]
    [InlineData("en", "Hello Sam, your table is ready.")]
    [InlineData("ar", "مرحباً Sam، طاولتك جاهزة.")]
    [InlineData("AR", "مرحباً Sam، طاولتك جاهزة.")]
    [InlineData("ar-EG", "مرحباً Sam، طاولتك جاهزة.")]
    public void Variant_is_chosen_by_the_recipient_language(string language, string expected)
    {
        _renderer.Render(Variants, Values, language).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    public void Unknown_language_falls_back_to_english(string? language)
    {
        _renderer.Render(Variants, Values, language).ShouldBe("Hello Sam, your table is ready.");
    }

    [Fact]
    public void Without_english_the_first_variant_by_language_code_is_used()
    {
        var variants = new Dictionary<string, string> { ["de"] = "Hallo {name}", ["ar"] = "مرحبا {name}" };

        _renderer.Render(variants, Values, "fr").ShouldBe("مرحبا Sam");
    }

    [Fact]
    public void A_placeholder_can_appear_more_than_once()
    {
        _renderer.Render(new Dictionary<string, string> { ["en"] = "{name} {name}" }, Values, "en").ShouldBe("Sam Sam");
    }

    [Fact]
    public void Missing_values_are_reported_together()
    {
        var variants = new Dictionary<string, string> { ["en"] = "{a} {b} {a} {c}" };

        var ex = Should.Throw<FreeTextRenderException>(
            () => _renderer.Render(variants, new Dictionary<string, string> { ["b"] = "x" }, "en"));

        ex.MissingPlaceholders.ShouldBe(["a", "c"]);
    }

    [Fact]
    public void Substituted_values_are_not_scanned_again()
    {
        var rendered = _renderer.Render(
            new Dictionary<string, string> { ["en"] = "Hi {name}" },
            new Dictionary<string, string> { ["name"] = "{other}" },
            "en");

        rendered.ShouldBe("Hi {other}");
    }

    [Fact]
    public void Text_without_placeholders_is_returned_unchanged()
    {
        _renderer.Render(new Dictionary<string, string> { ["en"] = "Plain {} text { x }" }, Values, "en")
            .ShouldBe("Plain {} text { x }");
    }

    [Fact]
    public void At_least_one_variant_is_required()
    {
        Should.Throw<ArgumentException>(() => _renderer.Render(new Dictionary<string, string>(), Values, "en"));
    }

    [Fact]
    public void Booking_free_text_is_composed_in_both_languages()
    {
        var composer = new BookingFreeTextComposer(_renderer, new DefaultStatusLabelMap());

        composer.ComposeBookingStatusUpdated(Snapshots.Hotel(BookingStatus.Cancelled, language: "en"))
            .ShouldBe("Hi Sam, the status of your booking REF-1001 is now: Cancelled.");
        composer.ComposeBookingStatusUpdated(Snapshots.Hotel(BookingStatus.Cancelled, language: "ar"))
            .ShouldBe("مرحباً Sam، حالة حجزك REF-1001 الآن: ملغي.");
        composer.ComposeBookingCreated(Snapshots.Hotel())
            .ShouldBe("Hi Sam, your Hotels booking with Acme Travel has been received. Check-in 2026-03-14. Created on 2026-03-01.");
    }
}

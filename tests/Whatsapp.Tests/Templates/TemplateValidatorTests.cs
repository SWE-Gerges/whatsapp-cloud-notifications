using Whatsapp.Templates;

namespace Whatsapp.Tests.Templates;

public class TemplateValidatorTests
{
    private static readonly TemplateDefinition Status = DefaultTemplates.BookingStatusUpdated;

    [Fact]
    public void Valid_data_has_no_errors()
    {
        TemplateValidator.Validate(Status, "en", ["Sam", "REF-1", "Confirmed"]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Wrong_parameter_count_is_reported(int count)
    {
        var errors = TemplateValidator.Validate(Status, "en", [.. Enumerable.Repeat("x", count)]);

        errors.ShouldHaveSingleItem().ShouldContain("Expected 3 parameters");
    }

    [Fact]
    public void Too_long_parameter_is_reported_with_its_limit()
    {
        var errors = TemplateValidator.Validate(Status, "en", [new string('a', 61), "REF", "Confirmed"]);

        errors.ShouldHaveSingleItem().ShouldContain("maximum is 60");
    }

    [Fact]
    public void A_parameter_exactly_at_the_limit_is_valid()
    {
        TemplateValidator.Validate(Status, "en", [new string('a', 60), "REF", "Confirmed"]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("line one\r\nline two")]
    [InlineData("tab\tseparated")]
    public void New_lines_and_tabs_are_rejected(string value)
    {
        var errors = TemplateValidator.Validate(Status, "en", [value, "REF", "Confirmed"]);

        errors.ShouldHaveSingleItem().ShouldContain("new lines or tabs");
    }

    [Theory]
    [InlineData("a    b")]
    [InlineData("a       b")]
    public void Four_or_more_consecutive_spaces_are_rejected(string value)
    {
        var errors = TemplateValidator.Validate(Status, "en", [value, "REF", "Confirmed"]);

        errors.ShouldHaveSingleItem().ShouldContain("four or more consecutive spaces");
    }

    [Fact]
    public void Three_consecutive_spaces_are_allowed()
    {
        TemplateValidator.Validate(Status, "en", ["a   b", "REF", "Confirmed"]).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_parameters_are_rejected(string value)
    {
        var errors = TemplateValidator.Validate(Status, "en", ["Sam", value, "Confirmed"]);

        errors.ShouldHaveSingleItem().ShouldContain("must not be empty");
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("ar", true)]
    [InlineData("AR", true)]
    [InlineData("ar-EG", true)]
    [InlineData("en_US", true)]
    [InlineData("fr", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Language_must_be_supported_by_the_template(string? language, bool supported)
    {
        var errors = TemplateValidator.Validate(Status, language, ["Sam", "REF", "Confirmed"]);

        (errors.Count == 0).ShouldBe(supported);
    }

    [Fact]
    public void Several_problems_are_reported_together()
    {
        var errors = TemplateValidator.Validate(Status, "fr", ["a\nb", "", new string('z', 100)]);

        errors.Count.ShouldBe(4);
    }

    [Fact]
    public void EnsureValid_throws_a_typed_exception_with_all_errors()
    {
        var ex = Should.Throw<TemplateValidationException>(
            () => TemplateValidator.EnsureValid(Status, "en", ["Sam"]));

        ex.TemplateName.ShouldBe("booking_status_updated");
        ex.Errors.ShouldNotBeEmpty();
    }
}

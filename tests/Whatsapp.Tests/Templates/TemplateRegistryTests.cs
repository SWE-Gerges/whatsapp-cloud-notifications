using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whatsapp.Templates;

namespace Whatsapp.Tests.Templates;

public class TemplateRegistryTests
{
    [Fact]
    public void Built_in_templates_have_the_documented_parameters()
    {
        var registry = new TemplateRegistry(DefaultTemplates.All);

        registry.Get("booking_created").Parameters.Select(p => p.Name)
            .ShouldBe(["customer_name", "company_name", "module", "created_date", "details"]);
        registry.Get("booking_status_updated").Parameters.Select(p => p.Name)
            .ShouldBe(["customer_name", "reference", "status"]);
    }

    [Fact]
    public void Unknown_template_is_reported_with_a_typed_exception()
    {
        var registry = new TemplateRegistry(DefaultTemplates.All);

        registry.TryGet("nope", out _).ShouldBeFalse();
        Should.Throw<TemplateNotRegisteredException>(() => registry.Get("nope")).TemplateName.ShouldBe("nope");
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        Should.Throw<ArgumentException>(
            () => new TemplateRegistry([DefaultTemplates.BookingCreated, DefaultTemplates.BookingCreated]));
    }

    [Fact]
    public void Templates_declared_in_configuration_are_added_to_the_built_in_ones()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Whatsapp:Templates:pickup_reminder:Languages:0"] = "en",
            ["Whatsapp:Templates:pickup_reminder:Parameters:0:Name"] = "time",
            ["Whatsapp:Templates:pickup_reminder:Parameters:0:MaxLength"] = "20",
        }).Build();
        var services = new ServiceCollection();
        services.AddWhatsappTemplates(configuration);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ITemplateRegistry>();

        registry.All.Count.ShouldBe(3);
        var custom = registry.Get("pickup_reminder");
        custom.Languages.ShouldBe(["en"]);
        custom.Parameters.ShouldHaveSingleItem().MaxLength.ShouldBe(20);
    }

    [Fact]
    public void A_configured_template_without_languages_stops_startup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Whatsapp:Templates:broken:Parameters:0:Name"] = "x",
        }).Build();

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddWhatsappTemplates(configuration));
    }

    [Fact]
    public void A_configured_template_cannot_replace_a_built_in_one()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Whatsapp:Templates:booking_created:Languages:0"] = "en",
        }).Build();

        Should.Throw<ArgumentException>(() => new ServiceCollection().AddWhatsappTemplates(configuration));
    }

    [Fact]
    public void Custom_status_labels_registered_first_win_over_the_default()
    {
        var custom = NSubstitute.Substitute.For<IStatusLabelMap>();
        var services = new ServiceCollection();
        services.AddSingleton(custom);
        services.AddWhatsappTemplates();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStatusLabelMap>().ShouldBeSameAs(custom);
    }
}

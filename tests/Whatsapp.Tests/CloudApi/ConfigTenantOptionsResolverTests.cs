using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;
using Whatsapp.CloudApi;

namespace Whatsapp.Tests.CloudApi;

public class ConfigTenantOptionsResolverTests
{
    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWhatsappCloudApi(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> ValidConfig() => new()
    {
        ["Whatsapp:Tenants:tenant-a:PhoneNumberId"] = "111",
        ["Whatsapp:Tenants:tenant-a:AccessTokenSecret"] = "tenant-a-token",
        ["Whatsapp:Tenants:tenant-a:BaseUrl"] = "https://graph.example.com/v1/",
        ["Whatsapp:Tenants:tenant-a:DefaultTemplateLanguage"] = "ar",
        ["Whatsapp:Tenants:tenant-b:Enabled"] = "false",
        ["Whatsapp:Tenants:tenant-c:PhoneNumberId"] = "333",
        ["Whatsapp:Tenants:tenant-c:AccessTokenSecret"] = "missing-secret",
        ["Whatsapp:Tenants:tenant-c:BaseUrl"] = "https://graph.example.com/v1",
        ["Whatsapp:Tenants:tenant-c:ServiceWindowEnabled"] = "false",
        ["Secrets:tenant-a-token"] = "secret-value",
    };

    [Fact]
    public async Task Resolves_an_enabled_tenant_with_its_secret()
    {
        await using var provider = Build(ValidConfig());
        var resolver = provider.GetRequiredService<ITenantOptionsResolver>();

        var options = await resolver.ResolveAsync("tenant-a", TestContext.Current.CancellationToken);

        options.Enabled.ShouldBeTrue();
        options.PhoneNumberId.ShouldBe("111");
        options.AccessToken.ShouldBe("secret-value");
        options.BaseUrl.ShouldBe("https://graph.example.com/v1");
        options.DefaultTemplateLanguage.ShouldBe("ar");
        options.ServiceWindowEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Every_call_returns_a_fresh_immutable_instance()
    {
        await using var provider = Build(ValidConfig());
        var resolver = provider.GetRequiredService<ITenantOptionsResolver>();

        var first = await resolver.ResolveAsync("tenant-a", TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync("tenant-a", TestContext.Current.CancellationToken);

        first.ShouldNotBeSameAs(second);
        first.ShouldBe(second);
    }

    [Fact]
    public async Task A_disabled_tenant_resolves_without_reading_any_secret()
    {
        await using var provider = Build(ValidConfig());
        var resolver = provider.GetRequiredService<ITenantOptionsResolver>();

        var options = await resolver.ResolveAsync("tenant-b", TestContext.Current.CancellationToken);

        options.Enabled.ShouldBeFalse();
        options.AccessToken.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_tenant_is_reported_with_a_typed_exception()
    {
        await using var provider = Build(ValidConfig());
        var resolver = provider.GetRequiredService<ITenantOptionsResolver>();

        var ex = await Should.ThrowAsync<TenantNotConfiguredException>(
            async () => await resolver.ResolveAsync("nobody", TestContext.Current.CancellationToken));

        ex.TenantId.ShouldBe("nobody");
    }

    [Fact]
    public async Task A_missing_secret_is_reported_with_a_typed_exception()
    {
        await using var provider = Build(ValidConfig());
        var resolver = provider.GetRequiredService<ITenantOptionsResolver>();

        var ex = await Should.ThrowAsync<SecretNotFoundException>(
            async () => await resolver.ResolveAsync("tenant-c", TestContext.Current.CancellationToken));

        ex.SecretName.ShouldBe("missing-secret");
    }

    [Fact]
    public async Task Service_window_flag_is_carried_per_tenant()
    {
        var config = ValidConfig();
        config["Secrets:missing-secret"] = "now-present";
        await using var provider = Build(config);
        var resolver = provider.GetRequiredService<ITenantOptionsResolver>();

        var options = await resolver.ResolveAsync("tenant-c", TestContext.Current.CancellationToken);

        options.ServiceWindowEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("PhoneNumberId")]
    [InlineData("AccessTokenSecret")]
    [InlineData("BaseUrl")]
    public void Startup_validation_rejects_an_enabled_tenant_with_missing_settings(string missing)
    {
        var config = ValidConfig();
        config.Remove($"Whatsapp:Tenants:tenant-a:{missing}");
        using var provider = Build(config);

        var ex = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<WhatsappTenantsOptions>>().CurrentValue);

        ex.Message.ShouldContain(missing);
    }

    [Fact]
    public void Startup_validation_rejects_a_relative_base_url()
    {
        var config = ValidConfig();
        config["Whatsapp:Tenants:tenant-a:BaseUrl"] = "/relative";
        using var provider = Build(config);

        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<WhatsappTenantsOptions>>().CurrentValue);
    }

    [Fact]
    public void Client_options_are_validated()
    {
        var config = ValidConfig();
        config["Whatsapp:Client:MaxRetryAttempts"] = "99";
        using var provider = Build(config);

        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<WhatsappClientOptions>>().Value);
    }
}

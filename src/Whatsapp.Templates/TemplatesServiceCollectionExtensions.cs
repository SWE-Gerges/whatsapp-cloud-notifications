using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whatsapp.Templates;

/// <summary>An extra template declared in configuration under <c>Whatsapp:Templates:{name}</c>.</summary>
public sealed class ConfiguredTemplate
{
    public List<string> Languages { get; set; } = [];

    public List<ConfiguredTemplateParameter> Parameters { get; set; } = [];
}

public sealed class ConfiguredTemplateParameter
{
    public string Name { get; set; } = string.Empty;

    public int MaxLength { get; set; } = 1024;
}

public static class TemplatesServiceCollectionExtensions
{
    public const string ConfigurationSection = "Whatsapp:Templates";

    /// <summary>
    /// Registers the template registry (built-in templates plus any declared in configuration),
    /// builder, free-text renderer and status labels. Replace <see cref="IStatusLabelMap"/> before this call to customise wording.
    /// </summary>
    public static IServiceCollection AddWhatsappTemplates(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var definitions = new List<TemplateDefinition>(DefaultTemplates.All);
        var configured = configuration?.GetSection(ConfigurationSection).Get<Dictionary<string, ConfiguredTemplate>>();
        if (configured is not null)
        {
            foreach (var (name, template) in configured)
            {
                if (template.Languages.Count == 0)
                {
                    throw new InvalidOperationException($"Template '{name}' in configuration declares no languages.");
                }

                definitions.Add(new TemplateDefinition(
                    name,
                    [.. template.Languages],
                    [.. template.Parameters.Select(p => new TemplateParameterDefinition(p.Name, p.MaxLength))]));
            }
        }

        // Validate eagerly so a duplicate or malformed template stops the host at startup.
        var registry = new TemplateRegistry(definitions);

        services.TryAddSingleton<ITemplateRegistry>(registry);
        services.TryAddSingleton<IStatusLabelMap, DefaultStatusLabelMap>();
        services.TryAddSingleton<FreeTextRenderer>();
        services.TryAddSingleton<ITemplateBuilder, TemplateBuilder>();
        services.TryAddSingleton<IBookingFreeTextComposer, BookingFreeTextComposer>();
        return services;
    }
}

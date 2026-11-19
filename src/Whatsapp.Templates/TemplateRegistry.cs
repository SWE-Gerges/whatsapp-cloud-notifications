using System.Diagnostics.CodeAnalysis;

namespace Whatsapp.Templates;

public static class TemplateNames
{
    public const string BookingCreated = "booking_created";

    public const string BookingStatusUpdated = "booking_status_updated";
}

/// <summary>Lookup of the templates the service may send.</summary>
public interface ITemplateRegistry
{
    IReadOnlyCollection<TemplateDefinition> All { get; }

    bool TryGet(string name, [NotNullWhen(true)] out TemplateDefinition? definition);

    /// <exception cref="TemplateNotRegisteredException">The template is unknown.</exception>
    TemplateDefinition Get(string name);
}

public sealed class TemplateNotRegisteredException(string name)
    : InvalidOperationException($"Template '{name}' is not registered.")
{
    public string TemplateName { get; } = name;
}

/// <summary>Immutable registry built once at startup.</summary>
public sealed class TemplateRegistry : ITemplateRegistry
{
    private readonly Dictionary<string, TemplateDefinition> _byName;

    public TemplateRegistry(IEnumerable<TemplateDefinition> definitions)
    {
        _byName = new Dictionary<string, TemplateDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!_byName.TryAdd(definition.Name, definition))
            {
                throw new ArgumentException($"Template '{definition.Name}' is registered more than once.", nameof(definitions));
            }
        }
    }

    public IReadOnlyCollection<TemplateDefinition> All => _byName.Values;

    public bool TryGet(string name, [NotNullWhen(true)] out TemplateDefinition? definition) =>
        _byName.TryGetValue(name, out definition);

    public TemplateDefinition Get(string name) =>
        _byName.TryGetValue(name, out var definition) ? definition : throw new TemplateNotRegisteredException(name);
}

/// <summary>The templates that ship with the service.</summary>
public static class DefaultTemplates
{
    public static TemplateDefinition BookingCreated { get; } = new(
        TemplateNames.BookingCreated,
        ["en", "ar"],
        [
            new TemplateParameterDefinition("customer_name", 60),
            new TemplateParameterDefinition("company_name", 60),
            new TemplateParameterDefinition("module", 30),
            new TemplateParameterDefinition("created_date", 20),
            new TemplateParameterDefinition("details", 200),
        ]);

    public static TemplateDefinition BookingStatusUpdated { get; } = new(
        TemplateNames.BookingStatusUpdated,
        ["en", "ar"],
        [
            new TemplateParameterDefinition("customer_name", 60),
            new TemplateParameterDefinition("reference", 60),
            new TemplateParameterDefinition("status", 60),
        ]);

    public static IReadOnlyList<TemplateDefinition> All { get; } = [BookingCreated, BookingStatusUpdated];
}

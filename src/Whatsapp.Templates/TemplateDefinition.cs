namespace Whatsapp.Templates;

/// <summary>One ordered, named body parameter of a template and its maximum length.</summary>
public sealed record TemplateParameterDefinition(string Name, int MaxLength);

/// <summary>An approved template as the service knows it: name, languages and ordered parameters.</summary>
public sealed record TemplateDefinition(
    string Name,
    IReadOnlyList<string> Languages,
    IReadOnlyList<TemplateParameterDefinition> Parameters)
{
    /// <summary>True when the template has a translation for <paramref name="languageCode"/> (matching ignores region and case).</summary>
    public bool SupportsLanguage(string? languageCode) => ResolveLanguage(languageCode) is not null;

    /// <summary>
    /// Returns the template's own language code that matches <paramref name="languageCode"/>
    /// (<c>ar-EG</c> matches <c>ar</c>), or null when the template has no such translation.
    /// </summary>
    public string? ResolveLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        var requested = LanguageCodes.Primary(languageCode);
        return Languages.FirstOrDefault(l => string.Equals(LanguageCodes.Primary(l), requested, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class LanguageCodes
{
    /// <summary>Primary language subtag of <c>en</c>, <c>en-GB</c> or <c>en_US</c>.</summary>
    public static string Primary(string code)
    {
        var trimmed = code.Trim();
        var separator = trimmed.AsSpan().IndexOfAny('-', '_');
        return separator < 0 ? trimmed : trimmed[..separator];
    }
}

using System.Text.RegularExpressions;

namespace Whatsapp.Templates;

public sealed class FreeTextRenderException(IReadOnlyList<string> missingPlaceholders)
    : InvalidOperationException($"No value was given for placeholder(s): {string.Join(", ", missingPlaceholders)}.")
{
    public IReadOnlyList<string> MissingPlaceholders { get; } = missingPlaceholders;
}

/// <summary>
/// Renders free-form messages: picks the variant that matches the recipient's language and substitutes
/// <c>{placeholder}</c> tokens. Substituted values are inserted verbatim and never re-scanned.
/// </summary>
public sealed partial class FreeTextRenderer
{
    public const string FallbackLanguage = "en";

    /// <param name="variants">Message patterns keyed by language code (<c>en</c>, <c>ar</c>, ...).</param>
    /// <param name="values">Values for the placeholders.</param>
    /// <param name="preferredLanguage">Recipient language. Falls back to English, then to the first variant.</param>
    /// <exception cref="FreeTextRenderException">A placeholder has no value.</exception>
    public string Render(
        IReadOnlyDictionary<string, string> variants,
        IReadOnlyDictionary<string, string> values,
        string? preferredLanguage)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(values);

        var pattern = SelectVariant(variants, preferredLanguage);
        var missing = new List<string>();

        var rendered = Placeholder().Replace(pattern, match =>
        {
            var name = match.Groups[1].Value;
            if (values.TryGetValue(name, out var value))
            {
                return value;
            }

            if (!missing.Contains(name, StringComparer.Ordinal))
            {
                missing.Add(name);
            }

            return match.Value;
        });

        return missing.Count > 0 ? throw new FreeTextRenderException(missing) : rendered;
    }

    private static string SelectVariant(IReadOnlyDictionary<string, string> variants, string? preferredLanguage)
    {
        if (variants.Count == 0)
        {
            throw new ArgumentException("At least one text variant is required.", nameof(variants));
        }

        if (TryFind(variants, preferredLanguage, out var preferred))
        {
            return preferred;
        }

        if (TryFind(variants, FallbackLanguage, out var fallback))
        {
            return fallback;
        }

        return variants.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).First().Value;
    }

    private static bool TryFind(IReadOnlyDictionary<string, string> variants, string? language, out string pattern)
    {
        pattern = string.Empty;
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        var wanted = LanguageCodes.Primary(language);
        foreach (var (key, value) in variants)
        {
            if (string.Equals(LanguageCodes.Primary(key), wanted, StringComparison.OrdinalIgnoreCase))
            {
                pattern = value;
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}

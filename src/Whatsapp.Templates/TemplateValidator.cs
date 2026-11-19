using System.Text.RegularExpressions;

namespace Whatsapp.Templates;

/// <summary>Raised when template data would be rejected by the Cloud API.</summary>
public sealed class TemplateValidationException(string templateName, IReadOnlyList<string> errors)
    : InvalidOperationException($"Template '{templateName}' data is invalid: {string.Join(" ", errors)}")
{
    public string TemplateName { get; } = templateName;

    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Checks template data before sending, so a mistake is found locally rather than as a provider error.</summary>
public static partial class TemplateValidator
{
    /// <summary>Returns every problem found; an empty list means the data is valid.</summary>
    public static IReadOnlyList<string> Validate(
        TemplateDefinition definition,
        string? languageCode,
        IReadOnlyList<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(parameters);

        var errors = new List<string>();

        if (!definition.SupportsLanguage(languageCode))
        {
            errors.Add($"Language '{languageCode}' is not supported by the template (supported: {string.Join(", ", definition.Languages)}).");
        }

        if (parameters.Count != definition.Parameters.Count)
        {
            errors.Add($"Expected {definition.Parameters.Count} parameters but got {parameters.Count}.");
            return errors;
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            var spec = definition.Parameters[i];
            var value = parameters[i];

            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Parameter {i + 1} ({spec.Name}) must not be empty.");
                continue;
            }

            if (value.Length > spec.MaxLength)
            {
                errors.Add($"Parameter {i + 1} ({spec.Name}) is {value.Length} characters, the maximum is {spec.MaxLength}.");
            }

            if (NewlineOrTab().IsMatch(value))
            {
                errors.Add($"Parameter {i + 1} ({spec.Name}) must not contain new lines or tabs.");
            }

            if (FourOrMoreSpaces().IsMatch(value))
            {
                errors.Add($"Parameter {i + 1} ({spec.Name}) must not contain four or more consecutive spaces.");
            }
        }

        return errors;
    }

    /// <exception cref="TemplateValidationException">The data is not valid.</exception>
    public static void EnsureValid(TemplateDefinition definition, string? languageCode, IReadOnlyList<string> parameters)
    {
        var errors = Validate(definition, languageCode, parameters);
        if (errors.Count > 0)
        {
            throw new TemplateValidationException(definition.Name, errors);
        }
    }

    [GeneratedRegex(@"[\r\n\t]")]
    private static partial Regex NewlineOrTab();

    [GeneratedRegex(@" {4,}")]
    private static partial Regex FourOrMoreSpaces();
}

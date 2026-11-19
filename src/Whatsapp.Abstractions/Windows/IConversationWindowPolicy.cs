namespace Whatsapp.Abstractions;

public enum WindowDecisionKind
{
    SendFreeText = 0,
    SendTemplate = 1,
    Skip = 2,
}

public sealed record WindowDecision(WindowDecisionKind Kind, string? Reason = null)
{
    public static WindowDecision SendFreeText { get; } = new(WindowDecisionKind.SendFreeText);

    public static WindowDecision SendTemplate { get; } = new(WindowDecisionKind.SendTemplate);

    public static WindowDecision Skip(string reason) => new(WindowDecisionKind.Skip, reason);
}

/// <summary>Applies the service-window rule of the Cloud API to a request.</summary>
public interface IConversationWindowPolicy
{
    /// <summary>
    /// A template request is always sent as a template. A free-text request is sent as text only while the
    /// window is open, otherwise it falls back to its template when one exists, otherwise it is skipped.
    /// </summary>
    Task<WindowDecision> DecideAsync(
        string tenantId,
        string recipient,
        NotificationKind kind,
        bool hasFallbackTemplate,
        bool serviceWindowEnabled,
        CancellationToken cancellationToken);
}

namespace Whatsapp.Abstractions;

/// <summary>How a notification is delivered to the recipient.</summary>
public enum NotificationKind
{
    /// <summary>An approved message template. Allowed at any time.</summary>
    Template = 0,

    /// <summary>Free-form text. Only accepted inside the 24 hour service window.</summary>
    FreeText = 1,
}

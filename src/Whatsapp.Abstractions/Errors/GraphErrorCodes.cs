namespace Whatsapp.Abstractions;

/// <summary>Graph error codes that the service acts on by number, in an API response and in a <c>failed</c> receipt alike.</summary>
public static class GraphErrorCodes
{
    /// <summary>Free-form text sent more than 24 hours after the recipient's last message.</summary>
    public const int OutsideServiceWindow = 131047;
}

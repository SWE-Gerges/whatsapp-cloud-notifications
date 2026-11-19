namespace Whatsapp.Events;

public sealed class EventDeliveryOptions
{
    public const string SectionName = "Whatsapp:Events";

    /// <summary>
    /// When true, booking events are queued as free text with the template as fallback, so customers with an open
    /// service window get plain text and everyone else gets the approved template. When false, templates are always used.
    /// </summary>
    public bool PreferFreeTextWithinWindow { get; set; }
}

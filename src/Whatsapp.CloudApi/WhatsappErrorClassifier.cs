using System.Net;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>
/// Maps a failed Graph API response to a <see cref="WhatsappErrorKind"/>. The Graph error code is the most
/// specific signal; the HTTP status is the fallback when no (known) code is present.
/// </summary>
internal static class WhatsappErrorClassifier
{
    public static WhatsappErrorKind Classify(HttpStatusCode statusCode, GraphError? error)
    {
        if (error?.Code is { } code && TryClassifyCode(code, error.Subcode, error.Message, out var kind))
        {
            return kind;
        }

        var status = (int)statusCode;
        return status switch
        {
            429 => WhatsappErrorKind.RateLimited,
            >= 500 => WhatsappErrorKind.ServerError,
            401 or 403 => WhatsappErrorKind.Authentication,
            _ => WhatsappErrorKind.InvalidRequest,
        };
    }

    private static bool TryClassifyCode(int code, int? subcode, string? message, out WhatsappErrorKind kind)
    {
        kind = code switch
        {
            190 => IsExpiredToken(subcode, message) ? WhatsappErrorKind.TokenExpired : WhatsappErrorKind.Authentication,

            // Throttling: app, user and page level limits, WABA limit, throughput, spam and pair rate limits.
            4 or 17 or 32 or 613 or 80007 or 130429 or 131048 or 131056 => WhatsappErrorKind.RateLimited,

            // Temporary provider side problems.
            1 or 2 or 131000 or 131016 or 131057 => WhatsappErrorKind.ServerError,

            // Permissions and account level authorisation problems.
            0 or 3 or 10 or 368 or 131005 or 131031 => WhatsappErrorKind.Authentication,
            >= 200 and <= 299 => WhatsappErrorKind.Authentication,

            // Recipient problems: undeliverable, same as sender, not in the allowed list.
            131021 or 131026 or 131030 => WhatsappErrorKind.InvalidRecipient,

            GraphErrorCodes.OutsideServiceWindow => WhatsappErrorKind.OutsideServiceWindow,

            // Template data does not match the approved template.
            131008 or 131009 or 132000 or 132005 or 132007 or 132012 or 132018 => WhatsappErrorKind.ParameterMismatch,

            // Template missing, paused, disabled or not approved.
            132001 or 132015 or 132016 or 132068 or 132069 => WhatsappErrorKind.TemplateUnavailable,

            _ => (WhatsappErrorKind)(-1),
        };

        return kind != (WhatsappErrorKind)(-1);
    }

    private static bool IsExpiredToken(int? subcode, string? message) =>
        subcode is 463 or 467
        || (message?.Contains("expired", StringComparison.OrdinalIgnoreCase) ?? false);
}

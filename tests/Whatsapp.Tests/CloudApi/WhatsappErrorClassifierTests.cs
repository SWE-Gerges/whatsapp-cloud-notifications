using System.Net;
using Whatsapp.Abstractions;
using Whatsapp.CloudApi;

namespace Whatsapp.Tests.CloudApi;

public class WhatsappErrorClassifierTests
{
    [Theory]
    // HTTP level fallbacks when there is no Graph envelope.
    [InlineData(429, null, null, null, WhatsappErrorKind.RateLimited)]
    [InlineData(500, null, null, null, WhatsappErrorKind.ServerError)]
    [InlineData(502, null, null, null, WhatsappErrorKind.ServerError)]
    [InlineData(503, null, null, null, WhatsappErrorKind.ServerError)]
    [InlineData(401, null, null, null, WhatsappErrorKind.Authentication)]
    [InlineData(403, null, null, null, WhatsappErrorKind.Authentication)]
    [InlineData(400, null, null, null, WhatsappErrorKind.InvalidRequest)]
    [InlineData(404, null, null, null, WhatsappErrorKind.InvalidRequest)]
    // Token problems.
    [InlineData(401, 190, 463, null, WhatsappErrorKind.TokenExpired)]
    [InlineData(401, 190, 467, null, WhatsappErrorKind.TokenExpired)]
    [InlineData(400, 190, null, "Error validating access token: Session has expired on Monday", WhatsappErrorKind.TokenExpired)]
    [InlineData(401, 190, 460, "Password changed", WhatsappErrorKind.Authentication)]
    [InlineData(403, 10, null, null, WhatsappErrorKind.Authentication)]
    [InlineData(400, 200, null, null, WhatsappErrorKind.Authentication)]
    [InlineData(400, 299, null, null, WhatsappErrorKind.Authentication)]
    [InlineData(400, 131005, null, null, WhatsappErrorKind.Authentication)]
    // Rate limits are transient.
    [InlineData(400, 4, null, null, WhatsappErrorKind.RateLimited)]
    [InlineData(400, 17, null, null, WhatsappErrorKind.RateLimited)]
    [InlineData(400, 80007, null, null, WhatsappErrorKind.RateLimited)]
    [InlineData(400, 130429, null, null, WhatsappErrorKind.RateLimited)]
    [InlineData(400, 131048, null, null, WhatsappErrorKind.RateLimited)]
    [InlineData(400, 131056, null, null, WhatsappErrorKind.RateLimited)]
    // Temporary provider side problems are transient.
    [InlineData(400, 1, null, null, WhatsappErrorKind.ServerError)]
    [InlineData(400, 2, null, null, WhatsappErrorKind.ServerError)]
    [InlineData(400, 131000, null, null, WhatsappErrorKind.ServerError)]
    [InlineData(400, 131016, null, null, WhatsappErrorKind.ServerError)]
    // Recipient problems are permanent.
    [InlineData(400, 131026, null, null, WhatsappErrorKind.InvalidRecipient)]
    [InlineData(400, 131021, null, null, WhatsappErrorKind.InvalidRecipient)]
    [InlineData(400, 131030, null, null, WhatsappErrorKind.InvalidRecipient)]
    // Service window.
    [InlineData(400, 131047, null, null, WhatsappErrorKind.OutsideServiceWindow)]
    // Template problems are permanent.
    [InlineData(404, 132001, null, null, WhatsappErrorKind.TemplateUnavailable)]
    [InlineData(400, 132015, null, null, WhatsappErrorKind.TemplateUnavailable)]
    [InlineData(400, 132016, null, null, WhatsappErrorKind.TemplateUnavailable)]
    [InlineData(400, 132000, null, null, WhatsappErrorKind.ParameterMismatch)]
    [InlineData(400, 132012, null, null, WhatsappErrorKind.ParameterMismatch)]
    [InlineData(400, 131008, null, null, WhatsappErrorKind.ParameterMismatch)]
    [InlineData(400, 131009, null, null, WhatsappErrorKind.ParameterMismatch)]
    // A known code wins over the HTTP status; an unknown code falls back to the status.
    [InlineData(500, 131026, null, null, WhatsappErrorKind.InvalidRecipient)]
    [InlineData(400, 99999, null, null, WhatsappErrorKind.InvalidRequest)]
    [InlineData(503, 99999, null, null, WhatsappErrorKind.ServerError)]
    public void Classifies_http_status_and_graph_code(int status, int? code, int? subcode, string? message, WhatsappErrorKind expected)
    {
        var error = code is null ? null : new GraphError(message, "OAuthException", code, subcode, "trace");

        WhatsappErrorClassifier.Classify((HttpStatusCode)status, error).ShouldBe(expected);
    }

    [Theory]
    [InlineData(WhatsappErrorKind.Network, true)]
    [InlineData(WhatsappErrorKind.Timeout, true)]
    [InlineData(WhatsappErrorKind.RateLimited, true)]
    [InlineData(WhatsappErrorKind.ServerError, true)]
    [InlineData(WhatsappErrorKind.InvalidRecipient, false)]
    [InlineData(WhatsappErrorKind.TemplateUnavailable, false)]
    [InlineData(WhatsappErrorKind.ParameterMismatch, false)]
    [InlineData(WhatsappErrorKind.Authentication, false)]
    [InlineData(WhatsappErrorKind.TokenExpired, false)]
    [InlineData(WhatsappErrorKind.OutsideServiceWindow, false)]
    [InlineData(WhatsappErrorKind.InvalidRequest, false)]
    [InlineData(WhatsappErrorKind.InvalidResponse, false)]
    public void Only_network_timeout_rate_limit_and_server_errors_are_transient(WhatsappErrorKind kind, bool transient)
    {
        kind.IsTransient().ShouldBe(transient);
        new WhatsappApiException(kind, "x").IsTransient.ShouldBe(transient);
    }
}

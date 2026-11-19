using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;
using Whatsapp.Webhooks;

namespace Whatsapp.Api.Endpoints;

/// <summary>
/// The inbound side: the verify-token handshake and signed deliveries. These routes are anonymous on purpose, since the
/// caller is the provider and not an API client; the verify token and the request signature are their authentication.
/// </summary>
internal static class WebhookEndpoints
{
    private const int MaxChallengeLength = 256;

    public static void MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/webhooks/whatsapp").WithTags("Webhooks").AllowAnonymous();

        group.MapGet("", VerifyAsync)
            .WithName("VerifyWebhook")
            .WithSummary("Webhook handshake: echoes hub.challenge when hub.verify_token matches a tenant's verify token.")
            .WithDescription("Called by the provider when a webhook is subscribed. Authenticated by the verify token, not by an API key. 403 for a wrong token or mode.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Security = [];
                return Task.CompletedTask;
            })
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("", ReceiveAsync)
            .Accepts<JsonElement>("application/json")
            .WithName("ReceiveWebhook")
            .WithSummary("Signed webhook delivery: inbound customer messages and delivery receipts.")
            .WithDescription(
                "Authenticated by X-Hub-Signature-256, the HMAC-SHA256 of the raw body with the tenant's app secret. The tenant is the one "
                + "that owns metadata.phone_number_id. A delivery for an unknown phone number is acknowledged and ignored. "
                + "Unknown fields and event types are skipped. Redeliveries change nothing.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Security = [];
                return Task.CompletedTask;
            })
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> VerifyAsync(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        IWebhookTenantDirectory tenants,
        WebhookMetrics metrics,
        CancellationToken cancellationToken)
    {
        // The token is compared even when the mode is wrong, so the answer does not reveal which part failed.
        var tokenMatches = !string.IsNullOrEmpty(verifyToken)
            && await tenants.IsValidVerifyTokenAsync(verifyToken, cancellationToken);

        var accepted = tokenMatches
            && string.Equals(mode, "subscribe", StringComparison.Ordinal)
            && challenge is { Length: > 0 and <= MaxChallengeLength };

        metrics.Verification(accepted);
        return accepted
            ? TypedResults.Text(challenge!, "text/plain")
            : TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The verification request was not accepted.");
    }

    private static async Task<Results<Ok, ProblemHttpResult>> ReceiveAsync(
        HttpRequest request,
        WhatsappWebhookProcessor processor,
        IOptions<WebhookOptions> options,
        CancellationToken cancellationToken)
    {
        var limit = options.Value.MaxBodyBytes;
        if (request.ContentLength > limit)
        {
            return TooLarge();
        }

        var body = await ReadBodyAsync(request.Body, limit, cancellationToken);
        if (body is null)
        {
            return TooLarge();
        }

        var signature = request.Headers[WebhookSignature.HeaderName];
        var result = await processor.HandleAsync(body, signature.Count == 1 ? signature[0] : null, cancellationToken);

        return result.Outcome == WebhookOutcome.Unauthorized
            ? TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The webhook signature is missing or not valid.")
            : TypedResults.Ok();
    }

    private static ProblemHttpResult TooLarge() =>
        TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The webhook body is too large.");

    /// <summary>Buffers the raw body, which the signature covers byte for byte. Returns null when it exceeds the limit.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Min(limit, 16 * 1024));
        while (true)
        {
            var memory = buffer.GetMemory(8 * 1024);
            var read = await stream.ReadAsync(memory, cancellationToken);
            if (read == 0)
            {
                return buffer.WrittenSpan.ToArray();
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                return null;
            }
        }
    }
}

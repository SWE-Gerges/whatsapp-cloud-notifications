using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;
using Whatsapp.Api.Contracts;
using Whatsapp.Api.Infrastructure;
using Whatsapp.Api.Security;
using Whatsapp.CloudApi;
using Whatsapp.Outbox;

namespace Whatsapp.Api.Endpoints;

internal static class AdminEndpoints
{
    private const string SimulatedType = "simulated";

    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);

    public static void MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin").WithTags("Admin");

        group.MapPost("/conversations/inbound", RecordInboundAsync)
            .RequireAuthorization(ApiPolicies.AdminKey)
            .WithName("SimulateInboundMessage")
            .WithSummary("DEMO ONLY: simulates an inbound customer message, which opens the 24 hour service window.")
            .WithDescription(
                "Demo only. Real inbound messages arrive through the signed webhook (POST /api/v1/webhooks/whatsapp). "
                + "This endpoint feeds the same recorder as the webhook, so it has exactly the same effect on the window.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<Results<Ok<InboundMessageResponse>, ValidationProblem>> RecordInboundAsync(
        InboundMessageRequest request,
        IInboundMessageRecorder inbound,
        IPhoneNumberNormalizer phones,
        IOptionsMonitor<WhatsappTenantsOptions> tenants,
        IOptions<ConversationWindowOptions> windowOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var now = timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(request.TenantId) || !tenants.CurrentValue.Tenants.ContainsKey(request.TenantId))
        {
            errors.Add("tenantId", "The tenant is not configured.");
        }

        var phone = phones.Normalize(null, request.Recipient, request.DefaultRegion);
        if (!phone.IsValid)
        {
            errors.Add("recipient", phone.FailureReason!);
        }

        var receivedAt = request.ReceivedAt ?? now;
        if (receivedAt > now + MaxClockSkew)
        {
            errors.Add("receivedAt", "The message cannot have been received in the future.");
        }

        if (errors.Any)
        {
            return TypedResults.ValidationProblem(errors.ToDictionary());
        }

        await inbound.RecordAsync(
            new InboundMessage(request.TenantId, $"simulated-{Guid.NewGuid():N}", phone.E164!, SimulatedType, receivedAt),
            cancellationToken);
        return TypedResults.Ok(new InboundMessageResponse(request.TenantId, phone.E164!, receivedAt + windowOptions.Value.Duration));
    }
}

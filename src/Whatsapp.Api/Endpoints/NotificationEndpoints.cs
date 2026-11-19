using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Whatsapp.Abstractions;
using Whatsapp.Api.Contracts;
using Whatsapp.Api.Infrastructure;
using Whatsapp.Api.Security;
using Whatsapp.Outbox;
using Whatsapp.Templates;

namespace Whatsapp.Api.Endpoints;

internal static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/notifications").WithTags("Notifications");

        group.MapPost("", EnqueueAsync)
            .RequireAuthorization(ApiPolicies.TenantKey)
            .WithName("EnqueueNotification")
            .WithSummary("Queues a custom notification for the tenant of the API key.")
            .WithDescription("Returns 202 for a new notification and 200 with duplicate=true when the same (recipient, eventKey, referenceKey) was already queued.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(ApiPolicies.AnyKey)
            .WithName("GetNotification")
            .WithSummary("Returns one notification with its receipt timeline and derived delivery status. A tenant key only sees its own notifications.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("", ListAsync)
            .RequireAuthorization(ApiPolicies.AdminKey)
            .WithName("ListNotifications")
            .WithSummary("Lists notifications of all tenants, newest first (admin). Filters: status, recipient, tenantId, deliveryStatus.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{id:guid}/retry", RetryAsync)
            .RequireAuthorization(ApiPolicies.AdminKey)
            .WithName("RetryNotification")
            .WithSummary("Requeues a Failed, DeadLettered or Unconfirmed notification with a fresh attempt budget (admin).")
            .WithDescription(
                "Retrying an Unconfirmed or unconfirmed-dead-lettered notification can deliver the message twice if the first request "
                + "had in fact been accepted; check the provider first.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Results<Accepted<EnqueueNotificationResponse>, Ok<EnqueueNotificationResponse>, ValidationProblem>> EnqueueAsync(
        EnqueueNotificationRequest request,
        ClaimsPrincipal user,
        INotificationEnqueuer enqueuer,
        IPhoneNumberNormalizer phones,
        ITemplateRegistry templates,
        FreeTextRenderer renderer,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var tenantId = user.GetTenantId()!;

        var phone = phones.Normalize(null, request.Recipient, request.DefaultRegion);
        if (!phone.IsValid)
        {
            errors.Add(nameof(request.Recipient).ToLowerInvariant(), phone.FailureReason!);
        }

        ValidateKeys(request, errors);
        var text = ValidateContent(request, templates, renderer, errors);

        if (errors.Any)
        {
            return TypedResults.ValidationProblem(errors.ToDictionary());
        }

        var result = await enqueuer.EnqueueAsync(
            new EnqueueNotification(
                tenantId,
                phone.E164!,
                request.EventKey,
                request.ReferenceKey,
                request.Kind,
                request.Template?.Name,
                request.Template?.LanguageCode,
                request.Template?.Parameters,
                text),
            cancellationToken);

        var response = new EnqueueNotificationResponse(result.Id, result.Status, result.Duplicate);
        return result.Duplicate
            ? TypedResults.Ok(response)
            : TypedResults.Accepted($"/api/v1/notifications/{result.Id}", response);
    }

    private static async Task<Results<Ok<NotificationResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        ClaimsPrincipal user,
        INotificationStore store,
        CancellationToken cancellationToken)
    {
        var tenantScope = user.IsAdmin() ? null : user.GetTenantId();
        var details = await store.GetDetailsAsync(id, tenantScope, cancellationToken);

        return details is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "The notification was not found.")
            : TypedResults.Ok(details.Notification.ToResponse(details.DeliveryStatus, details.Receipts));
    }

    private static async Task<Results<Ok<NotificationPageResponse>, ValidationProblem>> ListAsync(
        INotificationStore store,
        CancellationToken cancellationToken,
        NotificationStatus? status = null,
        string? recipient = null,
        string? tenantId = null,
        DeliveryStatus? deliveryStatus = null,
        int page = 1,
        int pageSize = 20)
    {
        var errors = new ValidationErrors();
        if (page < 1)
        {
            errors.Add("page", "The page must be 1 or greater.");
        }

        if (pageSize is < 1 or > 100)
        {
            errors.Add("pageSize", "The page size must be between 1 and 100.");
        }

        if (errors.Any)
        {
            return TypedResults.ValidationProblem(errors.ToDictionary());
        }

        var result = await store.ListAsync(new NotificationQuery(status, recipient, tenantId, page, pageSize, deliveryStatus), cancellationToken);
        return TypedResults.Ok(new NotificationPageResponse(
            [.. result.Items.Select(row => row.ToResponse(result.DeliveryStatuses.TryGetValue(row.Id, out var delivery) ? delivery : null))],
            result.Page,
            result.PageSize,
            result.Total));
    }

    private static async Task<Results<Accepted<RetryNotificationResponse>, ProblemHttpResult>> RetryAsync(
        Guid id,
        INotificationStore store,
        CancellationToken cancellationToken)
    {
        var outcome = await store.RetryAsync(id, cancellationToken);
        return outcome switch
        {
            RetryOutcome.Requeued => TypedResults.Accepted(
                $"/api/v1/notifications/{id}",
                new RetryNotificationResponse(id, NotificationStatus.Pending)),
            RetryOutcome.NotFound => TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "The notification was not found."),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Only Failed, DeadLettered or Unconfirmed notifications can be retried."),
        };
    }

    private static void ValidateKeys(EnqueueNotificationRequest request, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(request.EventKey))
        {
            errors.Add("eventKey", "The event key is required.");
        }
        else if (request.EventKey.Trim().Length > NotificationLog.MaxKeyLength)
        {
            errors.Add("eventKey", $"The event key must not exceed {NotificationLog.MaxKeyLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceKey))
        {
            errors.Add("referenceKey", "The reference key is required.");
        }
    }

    /// <summary>Validates template and text and returns the text to store (rendered, for free-text requests).</summary>
    private static string? ValidateContent(
        EnqueueNotificationRequest request,
        ITemplateRegistry templates,
        FreeTextRenderer renderer,
        ValidationErrors errors)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            errors.Add("kind", "The kind must be Template or FreeText.");
            return null;
        }

        ValidateTemplate(request, templates, errors);

        if (request.Kind == NotificationKind.Template)
        {
            if (request.Template is null)
            {
                errors.Add("template", "A template notification requires a template.");
            }

            if (request.Text is not null || request.TextVariants is not null)
            {
                errors.Add("text", "A template notification must not contain text.");
            }

            return null;
        }

        var hasText = !string.IsNullOrWhiteSpace(request.Text);
        var hasVariants = request.TextVariants is { Count: > 0 };
        if (hasText == hasVariants)
        {
            errors.Add("text", "Provide either text or textVariants.");
            return null;
        }

        try
        {
            var text = hasVariants
                ? renderer.Render(request.TextVariants!, request.Placeholders ?? [], request.PreferredLanguage)
                : request.Placeholders is { Count: > 0 }
                    ? renderer.Render(new Dictionary<string, string> { [FreeTextRenderer.FallbackLanguage] = request.Text! }, request.Placeholders, null)
                    : request.Text!;

            if (text.Length > NotificationLog.MaxTextLength)
            {
                errors.Add("text", $"The text must not exceed {NotificationLog.MaxTextLength} characters.");
            }

            return text;
        }
        catch (FreeTextRenderException ex)
        {
            errors.Add("placeholders", ex.Message);
            return null;
        }
    }

    private static void ValidateTemplate(EnqueueNotificationRequest request, ITemplateRegistry templates, ValidationErrors errors)
    {
        if (request.Template is not { } template)
        {
            return;
        }

        if (!templates.TryGet(template.Name, out var definition))
        {
            errors.Add("template.name", $"The template '{template.Name}' is not registered.");
            return;
        }

        // Without an explicit language the tenant default is used later, so only the data itself is checked here.
        var language = template.LanguageCode ?? definition.Languages[0];
        foreach (var problem in TemplateValidator.Validate(definition, language, template.Parameters))
        {
            errors.Add("template", problem);
        }
    }
}

internal static class NotificationMapper
{
    public static NotificationResponse ToResponse(
        this NotificationLog row,
        DeliveryStatus? deliveryStatus = null,
        IReadOnlyList<DeliveryReceipt>? receipts = null) => new(
        row.Id,
        row.TenantId,
        row.Recipient,
        row.EventKey,
        row.ReferenceKey,
        row.Kind,
        row.Status,
        row.Attempts,
        row.NextAttemptAt,
        row.TemplateName,
        row.TemplateLanguage,
        row.TemplateParameters,
        row.Text,
        row.ProviderMessageId,
        row.Error,
        row.CreatedAt,
        row.UpdatedAt,
        deliveryStatus,
        row.FallbackNotificationId,
        receipts?.Select(r => new DeliveryReceiptResponse(r.ProviderMessageId, r.Status, r.OccurredAt, r.ErrorCode, r.ErrorTitle)).ToList());
}

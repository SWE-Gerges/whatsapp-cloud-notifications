using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

/// <summary>
/// Claim-first enqueue. The row is inserted before anything is sent, and the unique index on
/// (tenant, recipient, event key, reference key) decides the race: exactly one caller creates the row,
/// every other caller receives it back as a duplicate. The insert is conflict-free (it reports a taken key through
/// the affected-row count instead of an exception), so the duplicate path is not an error anywhere.
/// </summary>
internal sealed partial class NotificationEnqueuer(
    IDbContextFactory<OutboxDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<NotificationEnqueuer> logger) : INotificationEnqueuer
{
    public async Task<EnqueueResult> EnqueueAsync(EnqueueNotification request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var row = CreateRow(request, timeProvider.GetUtcNow());

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var inserted = await ConflictFreeInsert.ExecuteAsync(
            db.Database,
            "NotificationLogs",
            InsertColumns,
            InsertValues(row),
            KeyColumns,
            cancellationToken).ConfigureAwait(false);

        if (inserted == 1)
        {
            return new EnqueueResult(row.Id, row.Status, Duplicate: false);
        }

        // The key was already claimed, so the insert changed nothing and raised nothing. Only the presence of the
        // row proves that; a missing row would mean the database ignored the insert for another reason.
        var existing = await db.NotificationLogs
            .AsNoTracking()
            .Where(x => x.TenantId == row.TenantId
                && x.Recipient == row.Recipient
                && x.EventKey == row.EventKey
                && x.ReferenceKey == row.ReferenceKey)
            .Select(x => new { x.Id, x.Status })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The notification insert was ignored but no row holds its key.");

        logger.LogInformation(
            "Notification {EventKey}/{ReferenceKey} for {Recipient} (tenant {TenantId}) was already claimed as {NotificationId}",
            row.EventKey,
            row.ReferenceKey,
            SensitiveData.MaskPhone(row.Recipient),
            row.TenantId,
            existing.Id);
        return new EnqueueResult(existing.Id, existing.Status, Duplicate: true);
    }

    /// <summary>The columns written by the claim insert; every other column is nullable and starts empty.</summary>
    internal static readonly IReadOnlyList<string> InsertColumns =
    [
        "Id", "TenantId", "Recipient", "EventKey", "ReferenceKey", "Kind", "TemplateName", "TemplateLanguage",
        "TemplateParameters", "Text", "Status", "Attempts", "NextAttemptAt", "CreatedAt", "UpdatedAt",
    ];

    internal static readonly IReadOnlyList<string> KeyColumns = ["TenantId", "Recipient", "EventKey", "ReferenceKey"];

    private static object?[] InsertValues(NotificationLog row) =>
    [
        row.Id,
        row.TenantId,
        row.Recipient,
        row.EventKey,
        row.ReferenceKey,
        row.Kind.ToString(),
        row.TemplateName,
        row.TemplateLanguage,
        OutboxDbContext.SerializeParameters(row.TemplateParameters),
        row.Text,
        row.Status.ToString(),
        row.Attempts,
        row.NextAttemptAt.UtcDateTime,
        row.CreatedAt.UtcDateTime,
        row.UpdatedAt.UtcDateTime,
    ];

    private static NotificationLog CreateRow(EnqueueNotification request, DateTimeOffset now)
    {
        var tenantId = Required(request.TenantId, "TenantId", 100);
        var recipient = request.Recipient?.Trim() ?? string.Empty;
        if (!E164().IsMatch(recipient))
        {
            throw new NotificationValidationException("Recipient must be a phone number in E.164 format.");
        }

        var eventKey = Required(request.EventKey, "EventKey", NotificationLog.MaxKeyLength);
        var referenceKey = Required(request.ReferenceKey, "ReferenceKey", int.MaxValue);
        if (referenceKey.Length > NotificationLog.MaxKeyLength)
        {
            referenceKey = referenceKey[..NotificationLog.MaxKeyLength];
        }

        var row = new NotificationLog
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Recipient = recipient,
            EventKey = eventKey,
            ReferenceKey = referenceKey,
            Kind = request.Kind,
            Status = NotificationStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var templateName = request.TemplateName?.Trim();
        if (!string.IsNullOrEmpty(templateName))
        {
            if (templateName.Length > NotificationLog.MaxTemplateNameLength)
            {
                throw new NotificationValidationException("TemplateName is too long.");
            }

            var language = request.TemplateLanguage?.Trim();
            if (language is { Length: > NotificationLog.MaxTemplateLanguageLength })
            {
                throw new NotificationValidationException("TemplateLanguage is too long.");
            }

            row.TemplateName = templateName;
            row.TemplateLanguage = string.IsNullOrEmpty(language) ? null : language;
            row.TemplateParameters = [.. request.TemplateParameters ?? []];
        }

        switch (request.Kind)
        {
            case NotificationKind.Template:
                if (row.TemplateName is null)
                {
                    throw new NotificationValidationException("A template notification requires a template name.");
                }

                break;

            case NotificationKind.FreeText:
                if (string.IsNullOrWhiteSpace(request.Text))
                {
                    throw new NotificationValidationException("A free-text notification requires text.");
                }

                if (request.Text.Length > NotificationLog.MaxTextLength)
                {
                    throw new NotificationValidationException($"Text must not exceed {NotificationLog.MaxTextLength} characters.");
                }

                row.Text = request.Text;
                break;

            default:
                throw new NotificationValidationException($"Unknown notification kind '{request.Kind}'.");
        }

        return row;
    }

    private static string Required(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new NotificationValidationException($"{name} is required.");
        }

        return trimmed.Length > maxLength
            ? throw new NotificationValidationException($"{name} must not exceed {maxLength} characters.")
            : trimmed;
    }

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();
}

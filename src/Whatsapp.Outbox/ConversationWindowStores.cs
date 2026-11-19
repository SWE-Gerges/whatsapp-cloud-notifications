using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

/// <summary>Process-local window store, for tests and single-instance demos.</summary>
public sealed class InMemoryConversationWindowStore(
    TimeProvider timeProvider,
    IOptions<ConversationWindowOptions> options) : IConversationWindowStore
{
    private readonly ConcurrentDictionary<(string Tenant, string Recipient), DateTimeOffset> _lastInbound = new();

    public Task<bool> IsOpenAsync(string tenantId, string recipient, CancellationToken cancellationToken)
    {
        var open = _lastInbound.TryGetValue((tenantId, recipient), out var last)
            && timeProvider.GetUtcNow() < last + options.Value.Duration;
        return Task.FromResult(open);
    }

    public Task RecordInboundAsync(string tenantId, string recipient, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        _lastInbound.AddOrUpdate((tenantId, recipient), receivedAt, (_, current) => receivedAt > current ? receivedAt : current);
        return Task.CompletedTask;
    }
}

/// <summary>Database backed window store shared by all instances of the service.</summary>
internal sealed class EfConversationWindowStore(
    IDbContextFactory<OutboxDbContext> dbFactory,
    TimeProvider timeProvider,
    IOptions<ConversationWindowOptions> options) : IConversationWindowStore
{
    public async Task<bool> IsOpenAsync(string tenantId, string recipient, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var last = await db.ConversationWindows
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Recipient == recipient)
            .Select(x => (DateTimeOffset?)x.LastInboundAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return last is { } value && timeProvider.GetUtcNow() < value + options.Value.Duration;
    }

    public async Task RecordInboundAsync(string tenantId, string recipient, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // One atomic statement: create the row, or move it forward when this message is newer. A late, replayed
        // or out-of-order message matches neither branch and changes nothing, so it can never shorten the window.
        // Concurrent callers need no retry and raise no key violation.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ConversationWindows" ("TenantId", "Recipient", "LastInboundAt") VALUES ({0}, {1}, {2})
            ON CONFLICT ("TenantId", "Recipient") DO UPDATE SET "LastInboundAt" = excluded."LastInboundAt"
            WHERE "ConversationWindows"."LastInboundAt" < excluded."LastInboundAt"
            """,
            [tenantId, recipient, receivedAt.UtcDateTime],
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ConversationWindowPolicy(IConversationWindowStore store) : IConversationWindowPolicy
{
    public async Task<WindowDecision> DecideAsync(
        string tenantId,
        string recipient,
        NotificationKind kind,
        bool hasFallbackTemplate,
        bool serviceWindowEnabled,
        CancellationToken cancellationToken)
    {
        if (kind == NotificationKind.Template)
        {
            return WindowDecision.SendTemplate;
        }

        if (!serviceWindowEnabled)
        {
            return WindowDecision.SendFreeText;
        }

        if (await store.IsOpenAsync(tenantId, recipient, cancellationToken).ConfigureAwait(false))
        {
            return WindowDecision.SendFreeText;
        }

        return hasFallbackTemplate
            ? WindowDecision.SendTemplate
            : WindowDecision.Skip("window closed");
    }
}

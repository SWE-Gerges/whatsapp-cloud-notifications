using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Outbox;

/// <summary>
/// The crash-after-accept problem: the provider accepted the message but the row never learned it. A fault-injecting
/// client plays the provider, which records every message it receives and then drops the answer.
/// </summary>
public sealed class UnconfirmedTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly OutboxHarness _harness = new();
    private readonly List<WhatsappMessage> _providerReceived = [];

    public void Dispose() => _harness.Dispose();

    private static WhatsappApiException Dropped() =>
        new(WhatsappErrorKind.Network, "The connection failed after the request may have been written.", outcomeUnknown: true);

    // NSubstitute runs the previous behaviour once, with default arguments, while a new one is being configured, so a
    // call without a message is not a request that reached the provider.
    private void ProviderReceives(NSubstitute.Core.CallInfo call)
    {
        if (call.ArgAt<WhatsappMessage>(0) is { } message)
        {
            lock (_providerReceived)
            {
                _providerReceived.Add(message);
            }
        }
    }

    /// <summary>The provider records the message and then the connection breaks, so the caller never sees the answer.</summary>
    private void AcceptThenDrop() =>
        _harness.ClientDoes(call =>
        {
            ProviderReceives(call);
            return Task.FromException<WhatsappSendResult>(Dropped());
        });

    /// <summary>The provider records the message and answers normally.</summary>
    private void Accept(string providerId) =>
        _harness.ClientDoes(call =>
        {
            ProviderReceives(call);
            return Task.FromResult(new WhatsappSendResult(providerId, null));
        });

    private Task<RecordResult> ReceiptAsync(Guid notificationId, string providerId, string status = ReceiptStatuses.Sent) =>
        _harness.Receipts.RecordAsync(
            new DeliveryReceiptInput("tenant-a", providerId, status, _harness.Time.GetUtcNow(), notificationId, null, null),
            Ct);

    private async Task<Guid> UnconfirmedAsync()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        AcceptThenDrop();
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);
        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Unconfirmed);
        return queued.Id;
    }

    private NotificationLog UnconfirmedRow(string reference, int attempts = 1) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = "tenant-a",
        Recipient = TestData.Recipient,
        EventKey = "booking-status-changed",
        ReferenceKey = reference,
        Kind = NotificationKind.Template,
        TemplateName = TemplateNames.BookingStatusUpdated,
        TemplateLanguage = "en",
        TemplateParameters = ["Sam", "REF-1", "Confirmed"],
        Status = NotificationStatus.Unconfirmed,
        Attempts = attempts,
        NextAttemptAt = _harness.Time.GetUtcNow().AddSeconds(-1),
        LastAttemptAt = _harness.Time.GetUtcNow().AddMinutes(-16),
        CreatedAt = _harness.Time.GetUtcNow().AddMinutes(-20),
        UpdatedAt = _harness.Time.GetUtcNow().AddMinutes(-16),
    };

    // ---- the failure classes ----

    [Fact]
    public async Task A_send_whose_connection_broke_after_the_request_was_written_becomes_unconfirmed_and_is_not_retried()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        AcceptThenDrop();
        var processor = _harness.CreateProcessor();

        await processor.ProcessBatchAsync(Ct);
        _harness.Time.Advance(TimeSpan.FromHours(3));
        await processor.ProcessBatchAsync(Ct);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Unconfirmed);
        row.Attempts.ShouldBe(1);
        row.ProviderMessageId.ShouldBeNull();
        row.NextAttemptAt.ShouldBe(row.UpdatedAt + _harness.Reconciliation.GracePeriod);
        row.Error.ShouldNotBeNull().ShouldStartWith("Unconfirmed: Network:");
        _providerReceived.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failure_that_provably_never_left_is_an_ordinary_retry()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(new WhatsappApiException(WhatsappErrorKind.Network, "The Cloud API could not be reached."));

        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.NextAttemptAt.ShouldBe(_harness.Time.GetUtcNow() + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_response_with_an_error_body_is_an_ordinary_retry_or_dead_letter_never_unconfirmed()
    {
        var transient = await _harness.EnqueueTemplateAsync("transient");
        _harness.FailWith(new WhatsappApiException(WhatsappErrorKind.ServerError, "down"));
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        var permanent = await _harness.EnqueueTemplateAsync("permanent");
        _harness.FailWith(new WhatsappApiException(WhatsappErrorKind.InvalidRecipient, "undeliverable"));
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        _harness.Database.Row(transient.Id).Status.ShouldBe(NotificationStatus.Pending);
        _harness.Database.Row(permanent.Id).Status.ShouldBe(NotificationStatus.DeadLettered);
    }

    // ---- a receipt resolves it ----

    [Fact]
    public async Task A_receipt_that_echoes_the_id_confirms_the_send_so_one_message_and_one_sent_row_remain()
    {
        var id = await UnconfirmedAsync();

        (await ReceiptAsync(id, "wamid.FAKE-1")).ShouldBe(RecordResult.Recorded);

        var row = _harness.Database.Row(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.ProviderMessageId.ShouldBe("wamid.FAKE-1");
        row.Error.ShouldBeNull();

        // The grace period passing, the reconciler and the dispatcher all find nothing left to do.
        _harness.Time.Advance(TimeSpan.FromHours(1));
        await _harness.CreateReconciler().RunOnceAsync(Ct);
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);
        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Sent);
        _providerReceived.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_confirmation_is_idempotent_with_the_normal_path()
    {
        var id = await UnconfirmedAsync();

        await ReceiptAsync(id, "wamid.FAKE-1", ReceiptStatuses.Sent);
        await ReceiptAsync(id, "wamid.FAKE-1", ReceiptStatuses.Delivered);
        await ReceiptAsync(id, "wamid.FAKE-1", ReceiptStatuses.Sent);

        var details = (await _harness.Store.GetDetailsAsync(id, null, Ct))!;
        details.Notification.Status.ShouldBe(NotificationStatus.Sent);
        details.Notification.ProviderMessageId.ShouldBe("wamid.FAKE-1");
        details.DeliveryStatus.ShouldBe(DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task A_failed_receipt_resolves_an_unconfirmed_row_to_failed_without_sending_again()
    {
        var id = await UnconfirmedAsync();

        await _harness.Receipts.RecordAsync(
            new DeliveryReceiptInput("tenant-a", "wamid.FAKE-1", ReceiptStatuses.Failed, _harness.Time.GetUtcNow(), id, 131026, "Message undeliverable"),
            Ct);

        var row = _harness.Database.Row(id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.ProviderMessageId.ShouldBe("wamid.FAKE-1");
        _providerReceived.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_receipt_that_arrives_while_the_send_is_in_flight_resolves_the_row_the_moment_it_becomes_unconfirmed()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.ClientDoes(async _ =>
        {
            await ReceiptAsync(queued.Id, "wamid.EARLY");
            throw Dropped();
        });

        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.ProviderMessageId.ShouldBe("wamid.EARLY");
    }

    [Fact]
    public async Task A_receipt_for_another_tenants_notification_id_does_not_confirm_anything()
    {
        var id = await UnconfirmedAsync();

        await _harness.Receipts.RecordAsync(
            new DeliveryReceiptInput("tenant-b", "wamid.OTHER", ReceiptStatuses.Sent, _harness.Time.GetUtcNow(), id, null, null),
            Ct);

        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Unconfirmed);
    }

    // ---- no receipt: the policy decides ----

    [Fact]
    public async Task Without_a_receipt_the_default_is_at_most_once_the_row_is_dead_lettered_and_never_sent_again()
    {
        var id = await UnconfirmedAsync();
        var reconciler = _harness.CreateReconciler();
        var processor = _harness.CreateProcessor();

        _harness.Time.Advance(_harness.Reconciliation.GracePeriod - TimeSpan.FromSeconds(1));
        (await reconciler.RunOnceAsync(Ct)).DeadLettered.ShouldBe(0);
        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Unconfirmed);

        _harness.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await reconciler.RunOnceAsync(Ct);
        await processor.ProcessBatchAsync(Ct);

        result.DeadLettered.ShouldBe(1);
        var row = _harness.Database.Row(id);
        row.Status.ShouldBe(NotificationStatus.DeadLettered);
        row.Error.ShouldNotBeNull().ShouldStartWith("unconfirmed:");
        _providerReceived.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_late_receipt_still_resolves_a_row_the_reconciler_already_dead_lettered()
    {
        var id = await UnconfirmedAsync();
        _harness.Time.Advance(_harness.Reconciliation.GracePeriod);
        await _harness.CreateReconciler().RunOnceAsync(Ct);
        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.DeadLettered);

        await ReceiptAsync(id, "wamid.LATE", ReceiptStatuses.Delivered);

        var row = _harness.Database.Row(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.ProviderMessageId.ShouldBe("wamid.LATE");
        row.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_receipt_the_at_least_once_policy_sends_the_message_again()
    {
        _harness.Reconciliation.OnGraceExpired = UnconfirmedPolicy.Resend;
        var id = await UnconfirmedAsync();
        _harness.Time.Advance(_harness.Reconciliation.GracePeriod);

        var result = await _harness.CreateReconciler().RunOnceAsync(Ct);
        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Pending);
        Accept("wamid.SECOND");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        result.Requeued.ShouldBe(1);
        var row = _harness.Database.Row(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.ProviderMessageId.ShouldBe("wamid.SECOND");
        row.Attempts.ShouldBe(2);
        _providerReceived.Count.ShouldBe(2, "the first message was accepted and the resend delivers it again: the cost of at-least-once");
    }

    [Fact]
    public async Task The_at_least_once_policy_is_bounded_by_the_attempt_budget()
    {
        _harness.Reconciliation.OnGraceExpired = UnconfirmedPolicy.Resend;
        var exhausted = UnconfirmedRow("exhausted", attempts: _harness.Options.MaxAttempts);
        _harness.Database.Insert(exhausted);

        var result = await _harness.CreateReconciler().RunOnceAsync(Ct);

        result.DeadLettered.ShouldBe(1);
        result.Requeued.ShouldBe(0);
        _harness.Database.Row(exhausted.Id).Status.ShouldBe(NotificationStatus.DeadLettered);
    }

    [Fact]
    public async Task A_resend_that_a_late_receipt_overtakes_is_never_sent()
    {
        _harness.Reconciliation.OnGraceExpired = UnconfirmedPolicy.Resend;
        var id = await UnconfirmedAsync();
        _harness.Time.Advance(_harness.Reconciliation.GracePeriod);
        await _harness.CreateReconciler().RunOnceAsync(Ct);
        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Pending);

        await ReceiptAsync(id, "wamid.FIRST");
        Accept("wamid.SECOND");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Sent);
        _harness.Database.Row(id).ProviderMessageId.ShouldBe("wamid.FIRST");
        _providerReceived.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_reconciler_applies_receipts_that_are_already_stored_before_it_dead_letters()
    {
        var row = UnconfirmedRow("stored-receipt");
        _harness.Database.Insert(row);

        // The receipt was stored but its effects were never applied (the process died in between).
        await using (var db = await _harness.Database.Factory.CreateDbContextAsync(Ct))
        {
            db.DeliveryReceipts.Add(new DeliveryReceipt
            {
                Id = Guid.CreateVersion7(),
                TenantId = "tenant-a",
                ProviderMessageId = "wamid.STORED",
                Status = ReceiptStatuses.Delivered,
                OccurredAt = _harness.Time.GetUtcNow().AddMinutes(-10),
                NotificationId = row.Id,
                RecordedAt = _harness.Time.GetUtcNow().AddMinutes(-10),
            });
            await db.SaveChangesAsync(Ct);
        }

        var result = await _harness.CreateReconciler().RunOnceAsync(Ct);

        result.DeadLettered.ShouldBe(0);
        var after = _harness.Database.Row(row.Id);
        after.Status.ShouldBe(NotificationStatus.Sent);
        after.ProviderMessageId.ShouldBe("wamid.STORED");
    }

    [Fact]
    public async Task A_receipt_from_before_the_current_cycle_does_not_confirm_a_retried_row()
    {
        var id = await UnconfirmedAsync();
        _harness.Time.Advance(_harness.Reconciliation.GracePeriod);
        await _harness.CreateReconciler().RunOnceAsync(Ct);
        _harness.Time.Advance(TimeSpan.FromMinutes(5));
        (await _harness.Store.RetryAsync(id, Ct)).ShouldBe(RetryOutcome.Requeued);

        // A receipt recorded before the retry began belongs to the earlier message.
        await using (var db = await _harness.Database.Factory.CreateDbContextAsync(Ct))
        {
            db.DeliveryReceipts.Add(new DeliveryReceipt
            {
                Id = Guid.CreateVersion7(),
                TenantId = "tenant-a",
                ProviderMessageId = "wamid.OLD",
                Status = ReceiptStatuses.Failed,
                OccurredAt = _harness.Time.GetUtcNow().AddMinutes(-3),
                NotificationId = id,
                ErrorCode = 131026,
                RecordedAt = _harness.Time.GetUtcNow().AddMinutes(-3),
            });
            await db.SaveChangesAsync(Ct);
        }

        await _harness.Effects.ApplyAsync(id, Ct);

        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Pending);
    }

    // ---- manual retry ----

    [Fact]
    public async Task An_administrator_can_retry_an_unconfirmed_row()
    {
        var id = await UnconfirmedAsync();

        (await _harness.Store.RetryAsync(id, Ct)).ShouldBe(RetryOutcome.Requeued);
        Accept("wamid.RETRY");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        var row = _harness.Database.Row(id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.ProviderMessageId.ShouldBe("wamid.RETRY");
        row.Attempts.ShouldBe(1, "a retry starts with a fresh attempt budget");
    }

    // ---- races ----

    [Fact]
    public async Task A_receipt_and_two_reconcilers_racing_for_one_row_leave_it_sent_and_never_resend_it()
    {
        for (var round = 0; round < 12; round++)
        {
            var row = UnconfirmedRow($"race-{round}");
            _harness.Database.Insert(row);

            await Task.WhenAll(
                _harness.CreateReconciler().RunOnceAsync(Ct),
                _harness.CreateReconciler().RunOnceAsync(Ct),
                ReceiptAsync(row.Id, $"wamid.R{round}"));

            var after = _harness.Database.Row(row.Id);
            after.Status.ShouldBe(NotificationStatus.Sent, $"round {round}");
            after.ProviderMessageId.ShouldBe($"wamid.R{round}");
        }

        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_reconciler_and_the_dispatcher_racing_on_the_same_rows_send_each_exactly_once_more()
    {
        _harness.Reconciliation.OnGraceExpired = UnconfirmedPolicy.Resend;
        Accept("wamid.RESEND");
        var rows = Enumerable.Range(0, 12).Select(i => UnconfirmedRow($"resend-{i}")).ToList();
        foreach (var row in rows)
        {
            _harness.Database.Insert(row);
        }

        for (var pass = 0; pass < 4; pass++)
        {
            await Task.WhenAll(
                _harness.CreateReconciler().RunOnceAsync(Ct),
                _harness.CreateProcessor().ProcessBatchAsync(Ct),
                _harness.CreateReconciler().RunOnceAsync(Ct));
        }

        _harness.Database.Rows().ShouldAllBe(r => r.Status == NotificationStatus.Sent && r.Attempts == 2);
        _providerReceived.Count.ShouldBe(12);
        _harness.SentMessages().Select(m => m.CallbackData).Distinct().Count().ShouldBe(12);
    }

    [Fact]
    public async Task Two_reconcilers_dead_letter_a_row_once()
    {
        var row = UnconfirmedRow("twice");
        _harness.Database.Insert(row);

        var results = await Task.WhenAll(
            _harness.CreateReconciler().RunOnceAsync(Ct),
            _harness.CreateReconciler().RunOnceAsync(Ct));

        results.Sum(r => r.DeadLettered).ShouldBe(1);
        _harness.Database.Row(row.Id).Status.ShouldBe(NotificationStatus.DeadLettered);
    }

    [Fact]
    public async Task Rows_that_are_not_unconfirmed_are_left_alone_by_the_reconciler()
    {
        var pending = await _harness.EnqueueTemplateAsync("pending");
        var sent = await _harness.EnqueueTemplateAsync("sent");
        _harness.Succeed("wamid.S");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);
        _harness.Time.Advance(TimeSpan.FromDays(2));

        var result = await _harness.CreateReconciler().RunOnceAsync(Ct);

        result.Examined.ShouldBe(0);
        _harness.Database.Row(pending.Id).Status.ShouldBe(NotificationStatus.Sent);
        _harness.Database.Row(sent.Id).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task The_reconciler_background_service_runs_passes_on_the_fake_clock_and_survives_an_error()
    {
        _harness.Reconciliation.PollInterval = TimeSpan.FromSeconds(10);
        var id = await UnconfirmedAsync();
        _harness.Time.Advance(_harness.Reconciliation.GracePeriod);

        // The first pass fails because the database is "down"; the loop logs it and tries again after the poll interval.
        var calls = 0;
        var flaky = Substitute.For<IDbContextFactory<OutboxDbContext>>();
        flaky.CreateDbContextAsync(TestContext.Current.CancellationToken).ReturnsForAnyArgs(call =>
            Interlocked.Increment(ref calls) == 1
                ? throw new InvalidOperationException("database is down")
                : _harness.Database.Factory.CreateDbContextAsync(call.Arg<CancellationToken>()));
        var logs = new CapturingLoggerProvider();
        var reconciler = new ReconciliationProcessor(
            flaky,
            _harness.Effects,
            Microsoft.Extensions.Options.Options.Create(_harness.Reconciliation),
            Microsoft.Extensions.Options.Options.Create(_harness.Options),
            _harness.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReconciliationProcessor>.Instance);
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var service = new ReconciliationService(
            reconciler,
            Microsoft.Extensions.Options.Options.Create(_harness.Reconciliation),
            _harness.Time,
            loggerFactory.CreateLogger<ReconciliationService>());

        await service.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 500 && _harness.Database.Row(id).Status != NotificationStatus.DeadLettered; i++)
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(Ct);

        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.DeadLettered);
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("reconciliation pass failed", StringComparison.Ordinal));
    }
}

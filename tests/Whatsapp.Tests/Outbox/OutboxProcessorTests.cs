using System.Net;
using Microsoft.EntityFrameworkCore;
using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Outbox;

public sealed class OutboxProcessorTests : IDisposable
{
    private readonly OutboxHarness _harness = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    public void Dispose() => _harness.Dispose();

    private static WhatsappApiException Transient(TimeSpan? retryAfter = null) =>
        new(WhatsappErrorKind.ServerError, "Service unavailable", HttpStatusCode.ServiceUnavailable, retryAfter: retryAfter);

    private static WhatsappApiException Permanent() =>
        new(WhatsappErrorKind.InvalidRecipient, "Message undeliverable", HttpStatusCode.BadRequest);

    [Fact]
    public async Task A_due_template_row_is_sent_and_marked_sent()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.Succeed("wamid.ABC");

        var processed = await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(1);
        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.ProviderMessageId.ShouldBe("wamid.ABC");
        row.Attempts.ShouldBe(1);
        row.Error.ShouldBeNull();
        row.LeaseToken.ShouldBeNull();
        row.LeaseExpiresAt.ShouldBeNull();

        var sent = _harness.SentMessages().ShouldHaveSingleItem().ShouldBeOfType<WhatsappTemplateMessage>();
        sent.To.ShouldBe(TestData.Recipient);
        sent.TemplateName.ShouldBe("booking_status_updated");
        sent.LanguageCode.ShouldBe("en");
        sent.Parameters.ShouldBe(["Sam", "REF-1", "Confirmed"]);
    }

    [Fact]
    public async Task The_client_receives_the_tenant_options_resolved_for_the_row()
    {
        await _harness.EnqueueTemplateAsync(tenantId: "tenant-z");

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var options = (WhatsappTenantOptions)_harness.Client.ReceivedCalls().Single().GetArguments()[1]!;
        options.TenantId.ShouldBe("tenant-z");
    }

    [Fact]
    public async Task The_template_language_defaults_to_the_tenant_language()
    {
        await _harness.EnqueueTemplateAsync(language: null);
        _harness.TenantIs(TestData.Tenant(language: "ar"));

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.SentMessages().ShouldHaveSingleItem().ShouldBeOfType<WhatsappTemplateMessage>().LanguageCode.ShouldBe("ar");
    }

    [Fact]
    public async Task Rows_that_are_not_due_yet_are_left_alone()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        var later = _harness.Time.GetUtcNow().AddSeconds(1);
        await using (var db = _harness.Database.Factory.CreateDbContext())
        {
            await db.NotificationLogs.Where(x => x.Id == queued.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, later), TestContext.Current.CancellationToken);
        }

        var processed = await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(0);
        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public async Task The_batch_size_limits_how_many_rows_are_claimed_oldest_first()
    {
        using var harness = new OutboxHarness(o => o.BatchSize = 2);
        for (var i = 0; i < 5; i++)
        {
            await harness.EnqueueTemplateAsync(reference: $"bk-{i}");
            harness.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var processed = await harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(2);
        harness.Database.Rows().Select(r => r.Status).ShouldBe(
            [NotificationStatus.Sent, NotificationStatus.Sent, NotificationStatus.Pending, NotificationStatus.Pending, NotificationStatus.Pending]);
    }

    [Fact]
    public async Task A_transient_failure_schedules_a_retry_with_exponential_backoff()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(Transient());
        var processor = _harness.CreateProcessor();
        var start = _harness.Time.GetUtcNow();

        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        var first = _harness.Database.Row(queued.Id);
        first.Status.ShouldBe(NotificationStatus.Pending);
        first.Attempts.ShouldBe(1);
        first.NextAttemptAt.ShouldBe(start + TimeSpan.FromSeconds(30));
        first.Error.ShouldNotBeNull().ShouldContain("ServerError");
        first.LeaseToken.ShouldBeNull();

        // Not yet due: nothing is claimed.
        _harness.Time.Advance(TimeSpan.FromSeconds(29));
        (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // Due: second failure doubles the delay.
        _harness.Time.Advance(TimeSpan.FromSeconds(1));
        var secondStart = _harness.Time.GetUtcNow();
        (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        var second = _harness.Database.Row(queued.Id);
        second.Attempts.ShouldBe(2);
        second.NextAttemptAt.ShouldBe(secondStart + TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_retry_that_succeeds_clears_the_earlier_error()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(Transient());
        var processor = _harness.CreateProcessor();
        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.Succeed("wamid.LATER");
        _harness.Time.Advance(TimeSpan.FromMinutes(1));
        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.Error.ShouldBeNull();
        row.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Jitter_keeps_the_delay_between_half_and_the_full_backoff()
    {
        _harness.Random = 0.0;
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(Transient());
        var start = _harness.Time.GetUtcNow();

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.Database.Row(queued.Id).NextAttemptAt.ShouldBe(start + TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_longer_retry_after_from_the_provider_overrides_the_backoff()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(new WhatsappApiException(
            WhatsappErrorKind.RateLimited,
            "Too many requests",
            HttpStatusCode.TooManyRequests,
            retryAfter: TimeSpan.FromMinutes(10)));
        var start = _harness.Time.GetUtcNow();

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.Database.Row(queued.Id).NextAttemptAt.ShouldBe(start + TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task The_row_is_dead_lettered_when_transient_failures_exhaust_the_attempts()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(Transient());
        var processor = _harness.CreateProcessor();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
            _harness.Time.Advance(TimeSpan.FromHours(2));
        }

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.DeadLettered);
        row.Attempts.ShouldBe(3);
        row.Error.ShouldNotBeNull().ShouldStartWith("Retries exhausted after 3 attempts");

        (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        _harness.SentMessages().Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_permanent_error_dead_letters_immediately_and_is_never_retried()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(Permanent());
        var processor = _harness.CreateProcessor();

        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);
        _harness.Time.Advance(TimeSpan.FromDays(1));
        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.DeadLettered);
        row.Attempts.ShouldBe(1);
        row.Error.ShouldNotBeNull().ShouldContain("InvalidRecipient");
        _harness.SentMessages().Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unexpected_exception_from_the_client_is_recorded_as_unconfirmed_not_thrown_and_not_sent_again()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.ClientDoes(_ => Task.FromException<WhatsappSendResult>(new InvalidOperationException("boom")));
        var processor = _harness.CreateProcessor();

        var processed = await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);
        _harness.Time.Advance(TimeSpan.FromHours(3));
        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(1);
        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Unconfirmed);
        row.Error.ShouldNotBeNull().ShouldContain("InvalidOperationException");
        _harness.SentMessages().Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unexpected_exception_before_the_request_is_sent_is_recorded_and_retried_not_thrown()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.TenantFails(new InvalidOperationException("boom"));

        var processed = await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(1);
        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.Error.ShouldNotBeNull().ShouldContain("InvalidOperationException");
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_notification_id_travels_with_the_message_as_callback_data()
    {
        var queued = await _harness.EnqueueTemplateAsync();

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.SentMessages().ShouldHaveSingleItem().CallbackData.ShouldBe(queued.Id.ToString("D"));
    }

    [Fact]
    public async Task A_disabled_tenant_is_skipped_without_calling_the_client()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.TenantIs(TestData.Tenant(enabled: false));

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Skipped);
        row.Error.ShouldBe("Tenant is disabled.");
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_tenant_fails_the_row_without_calling_the_client()
    {
        var queued = await _harness.EnqueueTemplateAsync(tenantId: "nobody");
        _harness.TenantFails(new TenantNotConfiguredException("nobody"));

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.Error.ShouldNotBeNull().ShouldContain("nobody");
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_secret_fails_the_row()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.TenantFails(new SecretNotFoundException("tenant-a-token"));

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Failed);
    }

    [Fact]
    public async Task An_unregistered_template_fails_the_row_without_calling_the_client()
    {
        var queued = await _harness.EnqueueTemplateAsync(templateName: "not_registered");

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.Error.ShouldNotBeNull().ShouldContain("not_registered");
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task Invalid_template_parameters_fail_the_row_before_reaching_the_provider()
    {
        var queued = await _harness.EnqueueTemplateAsync(parameters: ["only one"]);

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.Error.ShouldNotBeNull().ShouldContain("Expected 3 parameters");
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task Free_text_is_sent_as_text_inside_the_service_window()
    {
        var queued = await _harness.EnqueueFreeTextAsync();
        await _harness.WindowStore.RecordInboundAsync("tenant-a", TestData.Recipient, _harness.Time.GetUtcNow().AddHours(-3), Ct);

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Sent);
        _harness.SentMessages().ShouldHaveSingleItem().ShouldBeOfType<WhatsappTextMessage>().Body.ShouldBe("Your table is ready.");
    }

    [Fact]
    public async Task Free_text_outside_the_window_without_a_fallback_is_skipped()
    {
        var queued = await _harness.EnqueueFreeTextAsync();

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Skipped);
        row.Error.ShouldBe("window closed");
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task Free_text_outside_the_window_falls_back_to_its_template()
    {
        var queued = await _harness.EnqueueFreeTextAsync(fallbackTemplate: TemplateNames.BookingStatusUpdated);
        await _harness.WindowStore.RecordInboundAsync("tenant-a", TestData.Recipient, _harness.Time.GetUtcNow().AddHours(-25), Ct);

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Sent);
        _harness.SentMessages().ShouldHaveSingleItem().ShouldBeOfType<WhatsappTemplateMessage>()
            .TemplateName.ShouldBe("booking_status_updated");
    }

    [Fact]
    public async Task Tenants_that_do_not_enforce_the_window_send_text_directly()
    {
        var queued = await _harness.EnqueueFreeTextAsync();
        _harness.TenantIs(TestData.Tenant(serviceWindowEnabled: false));

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Sent);
        _harness.SentMessages().ShouldHaveSingleItem().ShouldBeOfType<WhatsappTextMessage>();
    }

    [Fact]
    public async Task A_row_stuck_in_sending_becomes_unconfirmed_once_its_lease_expired_and_is_not_sent_again()
    {
        var now = _harness.Time.GetUtcNow();
        var stuck = StuckRow(now, attempts: 1, leaseExpiresAt: now.AddSeconds(-1));
        _harness.Database.Insert(stuck);

        var processed = await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(0);
        var row = _harness.Database.Row(stuck.Id);
        row.Status.ShouldBe(NotificationStatus.Unconfirmed);
        row.Attempts.ShouldBe(1);
        row.NextAttemptAt.ShouldBe(now + _harness.Reconciliation.GracePeriod, "the reconcile deadline");
        row.LeaseToken.ShouldBeNull();
        row.Error.ShouldNotBeNull().ShouldStartWith("Unconfirmed:");
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_whose_lease_is_still_valid_is_not_touched()
    {
        var now = _harness.Time.GetUtcNow();
        var held = StuckRow(now, attempts: 1, leaseExpiresAt: now.AddMinutes(1));
        _harness.Database.Insert(held);

        var processed = await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(0);
        var row = _harness.Database.Row(held.Id);
        row.Status.ShouldBe(NotificationStatus.Sending);
        row.LeaseToken.ShouldBe(held.LeaseToken);
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_that_died_on_its_final_attempt_is_unconfirmed_like_any_other()
    {
        var now = _harness.Time.GetUtcNow();
        var stuck = StuckRow(now, attempts: 3, leaseExpiresAt: now.AddSeconds(-1));
        _harness.Database.Insert(stuck);

        var processed = await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        processed.ShouldBe(0);
        _harness.Database.Row(stuck.Id).Status.ShouldBe(NotificationStatus.Unconfirmed);
        _harness.SentMessages().ShouldBeEmpty();
    }

    [Fact]
    public async Task Shutdown_during_a_send_leaves_the_lease_which_later_makes_the_row_unconfirmed()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        using var shutdown = new CancellationTokenSource();
        _harness.ClientDoes(async _ =>
        {
            await shutdown.CancelAsync();
            throw new OperationCanceledException(shutdown.Token);
        });

        // Depending on timing the cancellation surfaces from the batch or is absorbed by the row; both are fine.
        try
        {
            await _harness.CreateProcessor().ProcessBatchAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            shutdown.IsCancellationRequested.ShouldBeTrue();
        }

        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Sending);

        // The interrupted request may have been written, so the row is not sent again: it waits for a receipt.
        _harness.Succeed("wamid.AFTER");
        _harness.Time.Advance(TimeSpan.FromMinutes(3));
        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Unconfirmed);
        row.ProviderMessageId.ShouldBeNull();
        _harness.SentMessages().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_cannot_overwrite_the_row()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.ClientDoes(_ =>
        {
            // Another worker re-claimed the row while this send was in flight.
            using var db = _harness.Database.Factory.CreateDbContext();
            db.NotificationLogs.Where(x => x.Id == queued.Id)
                .ExecuteUpdate(s => s.SetProperty(x => x.LeaseToken, (Guid?)Guid.NewGuid()));
            return Task.FromResult(new WhatsappSendResult("wamid.STALE", null));
        });

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Sending);
        row.ProviderMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task Two_dispatchers_never_send_the_same_row_twice()
    {
        using var harness = new OutboxHarness(o =>
        {
            o.BatchSize = 10;
            o.Concurrency = 4;
        });
        for (var i = 0; i < 60; i++)
        {
            await harness.EnqueueTemplateAsync(reference: $"bk-{i}");
        }

        var processors = Enumerable.Range(0, 3).Select(_ => harness.CreateProcessor()).ToArray();
        var total = 0;
        while (harness.Database.Rows().Any(r => r.Status == NotificationStatus.Pending))
        {
            var counts = await Task.WhenAll(processors.Select(p => p.ProcessBatchAsync(TestContext.Current.CancellationToken)));
            total += counts.Sum();
        }

        total.ShouldBe(60);
        harness.Database.Rows().ShouldAllBe(r => r.Status == NotificationStatus.Sent && r.Attempts == 1);
        harness.SentMessages().Count.ShouldBe(60);
    }

    [Fact]
    public async Task Stored_errors_are_limited_to_500_characters_and_mask_phone_numbers()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.FailWith(new WhatsappApiException(
            WhatsappErrorKind.InvalidRequest,
            "Failed for +12025550100 " + new string('x', 800),
            HttpStatusCode.BadRequest));

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var error = _harness.Database.Row(queued.Id).Error.ShouldNotBeNull();
        error.Length.ShouldBe(500);
        error.ShouldNotContain("12025550100");
        error.ShouldContain("+1202****0100");
    }

    private NotificationLog StuckRow(DateTimeOffset now, int attempts, DateTimeOffset leaseExpiresAt) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-a",
        Recipient = TestData.Recipient,
        EventKey = "booking-status-changed",
        ReferenceKey = "stuck",
        Kind = NotificationKind.Template,
        TemplateName = TemplateNames.BookingStatusUpdated,
        TemplateLanguage = "en",
        TemplateParameters = ["Sam", "REF-1", "Confirmed"],
        Status = NotificationStatus.Sending,
        Attempts = attempts,
        NextAttemptAt = now.AddMinutes(-5),
        LeaseToken = Guid.NewGuid(),
        LeaseExpiresAt = leaseExpiresAt,
        CreatedAt = now.AddMinutes(-10),
        UpdatedAt = now.AddMinutes(-5),
    };
}

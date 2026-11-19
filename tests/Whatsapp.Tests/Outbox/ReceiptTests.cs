using Microsoft.EntityFrameworkCore;
using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Outbox;

public sealed class DeliveryStatusDerivationTests
{
    public static TheoryData<string[], DeliveryStatus?> Cases => new()
    {
        { [], null },
        { ["sent"], DeliveryStatus.Sent },
        { ["sent", "delivered"], DeliveryStatus.Delivered },
        { ["delivered", "sent"], DeliveryStatus.Delivered },
        { ["read", "delivered", "sent"], DeliveryStatus.Read },
        { ["sent", "failed"], DeliveryStatus.Failed },
        { ["failed", "sent", "delivered", "read"], DeliveryStatus.Failed },
        { ["deleted"], null },
        { ["deleted", "sent"], DeliveryStatus.Sent },
        { ["SENT"], null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_status_is_derived_from_the_set_of_receipts(string[] statuses, DeliveryStatus? expected) =>
        DeliveryStatusDerivation.Derive(statuses).ShouldBe(expected);
}

/// <summary>
/// Delivery receipts against real rows: arrival order, receipts that beat the dispatcher, receipts for messages this
/// service did not send, the effects of a failure, and the template fallback.
/// </summary>
public sealed class ReceiptTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly OutboxHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly (string Status, int Seconds)[] Ladder =
    [
        (ReceiptStatuses.Sent, 1),
        (ReceiptStatuses.Delivered, 2),
        (ReceiptStatuses.Read, 3),
    ];

    private async Task<Guid> SentAsync(string providerId, string reference = "bk-1", string recipient = TestData.Recipient)
    {
        var queued = await _harness.EnqueueTemplateAsync(reference, recipient);
        _harness.Succeed(providerId);
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);
        return queued.Id;
    }

    private Task<RecordResult> ReceiptAsync(
        string providerId,
        string status,
        int seconds = 0,
        Guid? notificationId = null,
        int? code = null,
        string? title = null,
        string tenant = "tenant-a") =>
        _harness.Receipts.RecordAsync(
            new DeliveryReceiptInput(tenant, providerId, status, T0.AddSeconds(seconds), notificationId, code, title),
            Ct);

    private Task<RecordResult> FailureAsync(string providerId, int code = 131026, string title = "Message undeliverable", int seconds = 4, Guid? notificationId = null) =>
        ReceiptAsync(providerId, ReceiptStatuses.Failed, seconds, notificationId, code, title);

    private async Task<NotificationDetails> DetailsAsync(Guid id) =>
        (await _harness.Store.GetDetailsAsync(id, null, Ct))!;

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    // ---- ordering ----

    [Fact]
    public async Task Every_arrival_order_of_sent_delivered_and_read_ends_in_the_same_state()
    {
        var orders = Permutations(Ladder).ToList();
        orders.Count.ShouldBe(6);

        for (var i = 0; i < orders.Count; i++)
        {
            var id = await SentAsync($"wamid.L{i}", $"ladder-{i}");
            foreach (var (status, seconds) in orders[i])
            {
                await ReceiptAsync($"wamid.L{i}", status, seconds);
            }

            var details = await DetailsAsync(id);
            details.DeliveryStatus.ShouldBe(DeliveryStatus.Read, $"order {i}");
            details.Notification.Status.ShouldBe(NotificationStatus.Sent, $"order {i}");
            details.Receipts.Select(r => r.Status).ShouldBe(["sent", "delivered", "read"], $"order {i}: the timeline is in event order");
        }
    }

    [Fact]
    public async Task Failed_is_terminal_in_every_arrival_order_of_the_four_statuses()
    {
        (string Status, int Seconds)[] all = [.. Ladder, (ReceiptStatuses.Failed, 4)];
        var orders = Permutations(all).ToList();
        orders.Count.ShouldBe(24);

        for (var i = 0; i < orders.Count; i++)
        {
            var id = await SentAsync($"wamid.F{i}", $"failed-{i}");
            foreach (var (status, seconds) in orders[i])
            {
                if (status == ReceiptStatuses.Failed)
                {
                    await FailureAsync($"wamid.F{i}", seconds: seconds);
                }
                else
                {
                    await ReceiptAsync($"wamid.F{i}", status, seconds);
                }
            }

            var details = await DetailsAsync(id);
            details.DeliveryStatus.ShouldBe(DeliveryStatus.Failed, $"order {i}");
            details.Notification.Status.ShouldBe(NotificationStatus.Failed, $"order {i}");
            details.Notification.Error.ShouldNotBeNull().ShouldContain("131026");
        }
    }

    [Fact]
    public async Task A_late_sent_or_delivered_receipt_does_not_undo_a_failure()
    {
        var id = await SentAsync("wamid.A");
        await FailureAsync("wamid.A");

        await ReceiptAsync("wamid.A", ReceiptStatuses.Sent, 1);
        await ReceiptAsync("wamid.A", ReceiptStatuses.Delivered, 5);

        var details = await DetailsAsync(id);
        details.DeliveryStatus.ShouldBe(DeliveryStatus.Failed);
        details.Notification.Status.ShouldBe(NotificationStatus.Failed);
    }

    [Fact]
    public async Task A_receipt_status_this_service_does_not_know_is_stored_and_ignored_for_the_derived_status()
    {
        var id = await SentAsync("wamid.A");

        await ReceiptAsync("wamid.A", "deleted", 1);
        await ReceiptAsync("wamid.A", ReceiptStatuses.Delivered, 2);

        var details = await DetailsAsync(id);
        details.Receipts.Select(r => r.Status).ShouldBe(["deleted", "delivered"]);
        details.DeliveryStatus.ShouldBe(DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task A_redelivered_receipt_is_a_duplicate_and_adds_nothing()
    {
        await SentAsync("wamid.A");

        (await ReceiptAsync("wamid.A", ReceiptStatuses.Delivered, 2)).ShouldBe(RecordResult.Recorded);
        (await ReceiptAsync("wamid.A", ReceiptStatuses.Delivered, 2)).ShouldBe(RecordResult.Duplicate);
        (await ReceiptAsync("wamid.A", ReceiptStatuses.Delivered, 2)).ShouldBe(RecordResult.Duplicate);

        using var db = _harness.Database.Factory.CreateDbContext();
        db.DeliveryReceipts.Count().ShouldBe(1);
    }

    // ---- receipts that arrive before or without a notification ----

    [Fact]
    public async Task A_receipt_that_arrives_while_the_send_is_in_flight_is_joined_once_the_row_is_sent()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.ClientDoes(async _ =>
        {
            await ReceiptAsync("wamid.RACE", ReceiptStatuses.Sent, 1, queued.Id);
            return new WhatsappSendResult("wamid.RACE", null);
        });

        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        var details = await DetailsAsync(queued.Id);
        details.Notification.Status.ShouldBe(NotificationStatus.Sent);
        details.Notification.ProviderMessageId.ShouldBe("wamid.RACE");
        details.Receipts.ShouldHaveSingleItem().Status.ShouldBe("sent");
        details.DeliveryStatus.ShouldBe(DeliveryStatus.Sent);
    }

    [Fact]
    public async Task A_failure_that_arrives_while_the_send_is_in_flight_is_applied_as_soon_as_the_row_is_committed()
    {
        var queued = await _harness.EnqueueTemplateAsync();
        _harness.ClientDoes(async _ =>
        {
            await FailureAsync("wamid.RACE", notificationId: queued.Id);
            return new WhatsappSendResult("wamid.RACE", null);
        });

        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.ProviderMessageId.ShouldBe("wamid.RACE");
        row.Error.ShouldNotBeNull().ShouldContain("Message undeliverable");
    }

    [Fact]
    public async Task A_receipt_for_a_message_this_service_did_not_send_is_stored_and_joined_if_it_is_ever_sent()
    {
        (await ReceiptAsync("wamid.STRANGER", ReceiptStatuses.Delivered, 2)).ShouldBe(RecordResult.Recorded);
        (await ReceiptAsync("wamid.STRANGER", ReceiptStatuses.Read, 3)).ShouldBe(RecordResult.Recorded);
        _harness.Database.Rows().ShouldBeEmpty();

        var id = await SentAsync("wamid.STRANGER");

        var details = await DetailsAsync(id);
        details.Receipts.Select(r => r.Status).ShouldBe(["delivered", "read"]);
        details.DeliveryStatus.ShouldBe(DeliveryStatus.Read);
    }

    [Fact]
    public async Task A_receipt_never_reaches_a_notification_of_another_tenant()
    {
        var id = await SentAsync("wamid.A");

        // The same provider message id and even the same notification id, but reported for another tenant's number.
        await ReceiptAsync("wamid.A", ReceiptStatuses.Read, 3, id, tenant: "tenant-b");
        await ReceiptAsync("wamid.A", ReceiptStatuses.Failed, 4, id, 131026, "Message undeliverable", tenant: "tenant-b");

        var details = await DetailsAsync(id);
        details.Receipts.ShouldBeEmpty();
        details.DeliveryStatus.ShouldBeNull();
        details.Notification.Status.ShouldBe(NotificationStatus.Sent);

        // Nothing is lost: the receipts are stored, just not joined.
        using var db = _harness.Database.Factory.CreateDbContext();
        db.DeliveryReceipts.Count(r => r.TenantId == "tenant-b").ShouldBe(2);
    }

    // ---- effects of a failure ----

    [Fact]
    public async Task A_failed_receipt_marks_the_notification_failed_with_the_reason_and_it_is_not_sent_again()
    {
        var id = await SentAsync("wamid.A");

        await FailureAsync("wamid.A", 131026, "Message undeliverable");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        var row = _harness.Database.Row(id);
        row.Status.ShouldBe(NotificationStatus.Failed);
        row.Error.ShouldBe("Delivery failed: 131026 Message undeliverable");
        row.ProviderMessageId.ShouldBe("wamid.A");
        _harness.SentMessages().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_receipt_without_a_code_is_still_a_failure()
    {
        var id = await SentAsync("wamid.A");

        await ReceiptAsync("wamid.A", ReceiptStatuses.Failed, 4);

        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Failed);
        _harness.Database.Row(id).Error.ShouldBe("Delivery failed: no code");
    }

    [Fact]
    public async Task A_failed_notification_can_be_retried_and_the_old_receipts_do_not_decide_the_retry()
    {
        var id = await SentAsync("wamid.OLD");
        await ReceiptAsync("wamid.OLD", ReceiptStatuses.Sent, 1, id);
        await FailureAsync("wamid.OLD", notificationId: id);
        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Failed);

        _harness.Time.Advance(TimeSpan.FromMinutes(1));
        (await _harness.Store.RetryAsync(id, Ct)).ShouldBe(RetryOutcome.Requeued);

        // The provider redelivers the old failure; it names the old message and must not touch the new cycle.
        await FailureAsync("wamid.OLD", notificationId: id);
        await ReceiptAsync("wamid.OLD", ReceiptStatuses.Sent, 1, id);
        _harness.Database.Row(id).Status.ShouldBe(NotificationStatus.Pending);
        (await DetailsAsync(id)).DeliveryStatus.ShouldBeNull();

        _harness.Succeed("wamid.NEW");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);
        _harness.Time.Advance(TimeSpan.FromSeconds(1));
        await ReceiptAsync("wamid.NEW", ReceiptStatuses.Delivered, 70, id);

        var details = await DetailsAsync(id);
        details.Notification.Status.ShouldBe(NotificationStatus.Sent);
        details.Notification.ProviderMessageId.ShouldBe("wamid.NEW");
        details.DeliveryStatus.ShouldBe(DeliveryStatus.Delivered);
        details.Receipts.ShouldHaveSingleItem().ProviderMessageId.ShouldBe("wamid.NEW");
    }

    // ---- template fallback ----

    private async Task<Guid> SentFreeTextAsync(string? fallbackTemplate)
    {
        await _harness.WindowStore.RecordInboundAsync("tenant-a", TestData.Recipient, _harness.Time.GetUtcNow(), Ct);
        var queued = await _harness.EnqueueFreeTextAsync(fallbackTemplate: fallbackTemplate);
        _harness.Succeed("wamid.TEXT");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);
        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Sent);
        return queued.Id;
    }

    [Fact]
    public async Task A_window_error_on_a_free_text_notification_queues_its_template_exactly_once()
    {
        var id = await SentFreeTextAsync(TemplateNames.BookingStatusUpdated);

        await FailureAsync("wamid.TEXT", GraphErrorCodes.OutsideServiceWindow, "Re-engagement message", notificationId: id);
        await FailureAsync("wamid.TEXT", GraphErrorCodes.OutsideServiceWindow, "Re-engagement message", notificationId: id);
        await FailureAsync("wamid.TEXT", GraphErrorCodes.OutsideServiceWindow, "Re-engagement message", seconds: 9);

        var rows = _harness.Database.Rows();
        rows.Count.ShouldBe(2);
        var original = rows.Single(r => r.Id == id);
        var fallback = rows.Single(r => r.Id != id);
        original.Status.ShouldBe(NotificationStatus.Failed);
        original.FallbackNotificationId.ShouldBe(fallback.Id);
        original.Error.ShouldNotBeNull().ShouldContain("template version was queued");
        fallback.Kind.ShouldBe(NotificationKind.Template);
        fallback.EventKey.ShouldBe(ReceiptEffects.FallbackEventKey);
        fallback.ReferenceKey.ShouldBe(id.ToString("D"));
        fallback.TemplateName.ShouldBe(TemplateNames.BookingStatusUpdated);
        fallback.TemplateParameters.ShouldBe(["Sam", "REF-1", "Confirmed"]);

        _harness.Succeed("wamid.TEMPLATE");
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);
        await _harness.CreateProcessor().ProcessBatchAsync(Ct);

        _harness.SentMessages().Select(m => m.GetType()).ShouldBe([typeof(WhatsappTextMessage), typeof(WhatsappTemplateMessage)]);
        _harness.Database.Row(fallback.Id).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task A_window_error_without_a_fallback_template_only_fails_the_notification()
    {
        var id = await SentFreeTextAsync(fallbackTemplate: null);

        await FailureAsync("wamid.TEXT", GraphErrorCodes.OutsideServiceWindow, "Re-engagement message", notificationId: id);

        _harness.Database.Rows().ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.Failed);
        _harness.Database.Row(id).FallbackNotificationId.ShouldBeNull();
    }

    [Fact]
    public async Task Another_failure_code_does_not_queue_a_fallback()
    {
        var id = await SentFreeTextAsync(TemplateNames.BookingStatusUpdated);

        await FailureAsync("wamid.TEXT", 131026, "Message undeliverable", notificationId: id);

        _harness.Database.Rows().ShouldHaveSingleItem().FallbackNotificationId.ShouldBeNull();
    }

    // ---- list filter ----

    [Fact]
    public async Task The_list_filter_the_page_and_the_single_notification_agree_on_the_delivery_status()
    {
        var none = await SentAsync("wamid.NONE", "none");
        var sentOnly = await SentAsync("wamid.S", "s");
        var delivered = await SentAsync("wamid.D", "d");
        var read = await SentAsync("wamid.R", "r");
        var failed = await SentAsync("wamid.X", "x");
        var unknown = await SentAsync("wamid.U", "u");

        await ReceiptAsync("wamid.S", ReceiptStatuses.Sent, 1);
        await ReceiptAsync("wamid.D", ReceiptStatuses.Delivered, 2);
        await ReceiptAsync("wamid.D", ReceiptStatuses.Sent, 1);
        await ReceiptAsync("wamid.R", ReceiptStatuses.Read, 3);
        await ReceiptAsync("wamid.R", ReceiptStatuses.Delivered, 2);
        await ReceiptAsync("wamid.X", ReceiptStatuses.Delivered, 2);
        await FailureAsync("wamid.X");
        await ReceiptAsync("wamid.U", "deleted", 1);

        var expected = new Dictionary<DeliveryStatus, Guid>
        {
            [DeliveryStatus.Sent] = sentOnly,
            [DeliveryStatus.Delivered] = delivered,
            [DeliveryStatus.Read] = read,
            [DeliveryStatus.Failed] = failed,
        };

        foreach (var (status, id) in expected)
        {
            var page = await _harness.Store.ListAsync(new NotificationQuery(DeliveryStatus: status), Ct);
            page.Items.Select(r => r.Id).ShouldBe([id], $"filter {status}");
            page.Total.ShouldBe(1);
            page.DeliveryStatuses[id].ShouldBe(status);
            (await DetailsAsync(id)).DeliveryStatus.ShouldBe(status);
        }

        var all = await _harness.Store.ListAsync(new NotificationQuery(), Ct);
        all.Items.Count.ShouldBe(6);
        all.DeliveryStatuses.Keys.ShouldBe([sentOnly, delivered, read, failed], ignoreOrder: true);
        (await DetailsAsync(none)).DeliveryStatus.ShouldBeNull();
        (await DetailsAsync(unknown)).DeliveryStatus.ShouldBeNull();
    }

    [Fact]
    public async Task A_tenant_only_sees_its_own_receipts_on_a_notification()
    {
        var id = await SentAsync("wamid.A");
        await ReceiptAsync("wamid.A", ReceiptStatuses.Read, 3, id);

        (await _harness.Store.GetDetailsAsync(id, "tenant-a", Ct)).ShouldNotBeNull().Receipts.Count.ShouldBe(1);
        (await _harness.Store.GetDetailsAsync(id, "tenant-b", Ct)).ShouldBeNull();
    }
}

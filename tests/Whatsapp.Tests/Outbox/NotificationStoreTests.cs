using Microsoft.EntityFrameworkCore;
using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Outbox;

public sealed class NotificationStoreTests : IDisposable
{
    private readonly OutboxHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task SetStatusAsync(Guid id, NotificationStatus status, int attempts = 0)
    {
        await using var db = _harness.Database.Factory.CreateDbContext();
        await db.NotificationLogs.Where(x => x.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, status).SetProperty(x => x.Attempts, attempts),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Get_returns_the_row_and_hides_other_tenants_rows()
    {
        var queued = await _harness.EnqueueTemplateAsync(tenantId: "tenant-a");

        (await _harness.Store.GetAsync(queued.Id, "tenant-a", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await _harness.Store.GetAsync(queued.Id, "tenant-b", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await _harness.Store.GetAsync(queued.Id, null, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await _harness.Store.GetAsync(Guid.NewGuid(), null, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task List_filters_by_status_recipient_and_tenant_newest_first()
    {
        var first = await _harness.EnqueueTemplateAsync("bk-1");
        _harness.Time.Advance(TimeSpan.FromSeconds(1));
        var second = await _harness.EnqueueTemplateAsync("bk-2", recipient: TestData.OtherRecipient);
        _harness.Time.Advance(TimeSpan.FromSeconds(1));
        var third = await _harness.EnqueueTemplateAsync("bk-3", tenantId: "tenant-b");
        await SetStatusAsync(first.Id, NotificationStatus.Sent);

        var all = await _harness.Store.ListAsync(new NotificationQuery(), TestContext.Current.CancellationToken);
        all.Total.ShouldBe(3);
        all.Items.Select(i => i.Id).ShouldBe([third.Id, second.Id, first.Id]);

        (await _harness.Store.ListAsync(new NotificationQuery(Status: NotificationStatus.Sent), TestContext.Current.CancellationToken))
            .Items.Select(i => i.Id).ShouldBe([first.Id]);
        (await _harness.Store.ListAsync(new NotificationQuery(Recipient: TestData.OtherRecipient), TestContext.Current.CancellationToken))
            .Items.Select(i => i.Id).ShouldBe([second.Id]);
        (await _harness.Store.ListAsync(new NotificationQuery(TenantId: "tenant-b"), TestContext.Current.CancellationToken))
            .Items.Select(i => i.Id).ShouldBe([third.Id]);
    }

    [Fact]
    public async Task List_pages_and_clamps_the_page_size()
    {
        for (var i = 0; i < 5; i++)
        {
            await _harness.EnqueueTemplateAsync($"bk-{i}");
            _harness.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var page2 = await _harness.Store.ListAsync(new NotificationQuery(Page: 2, PageSize: 2), TestContext.Current.CancellationToken);
        page2.Items.Count.ShouldBe(2);
        page2.Total.ShouldBe(5);
        page2.Page.ShouldBe(2);

        var clamped = await _harness.Store.ListAsync(new NotificationQuery(Page: 0, PageSize: 10_000), TestContext.Current.CancellationToken);
        clamped.Page.ShouldBe(1);
        clamped.PageSize.ShouldBe(100);
    }

    [Theory]
    [InlineData(NotificationStatus.Failed)]
    [InlineData(NotificationStatus.DeadLettered)]
    public async Task Retry_requeues_failed_and_dead_lettered_rows_with_a_fresh_attempt_budget(NotificationStatus status)
    {
        var queued = await _harness.EnqueueTemplateAsync();
        await SetStatusAsync(queued.Id, status, attempts: 3);
        _harness.Time.Advance(TimeSpan.FromMinutes(5));

        var outcome = await _harness.Store.RetryAsync(queued.Id, TestContext.Current.CancellationToken);

        outcome.ShouldBe(RetryOutcome.Requeued);
        var row = _harness.Database.Row(queued.Id);
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.Attempts.ShouldBe(0);
        row.NextAttemptAt.ShouldBe(_harness.Time.GetUtcNow());

        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);
        _harness.Database.Row(queued.Id).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Theory]
    [InlineData(NotificationStatus.Pending)]
    [InlineData(NotificationStatus.Sending)]
    [InlineData(NotificationStatus.Sent)]
    [InlineData(NotificationStatus.Skipped)]
    public async Task Retry_refuses_rows_in_any_other_state(NotificationStatus status)
    {
        var queued = await _harness.EnqueueTemplateAsync();
        await SetStatusAsync(queued.Id, status);

        (await _harness.Store.RetryAsync(queued.Id, TestContext.Current.CancellationToken)).ShouldBe(RetryOutcome.NotRetryable);
        _harness.Database.Row(queued.Id).Status.ShouldBe(status);
    }

    [Fact]
    public async Task Retry_reports_unknown_ids()
    {
        (await _harness.Store.RetryAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)).ShouldBe(RetryOutcome.NotFound);
    }

    [Fact]
    public async Task Template_parameters_round_trip_through_the_database()
    {
        var queued = await _harness.EnqueueTemplateAsync(
            templateName: TemplateNames.BookingStatusUpdated,
            parameters: ["Zoë", "REF \"1\"", "مؤكد"]);

        var row = await _harness.Store.GetAsync(queued.Id, null, TestContext.Current.CancellationToken);

        row.ShouldNotBeNull().TemplateParameters.ShouldBe(["Zoë", "REF \"1\"", "مؤكد"]);
    }
}

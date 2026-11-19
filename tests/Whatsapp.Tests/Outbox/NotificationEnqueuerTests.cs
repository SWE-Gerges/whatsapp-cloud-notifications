using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Templates;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Outbox;

public sealed class NotificationEnqueuerTests : IDisposable
{
    private readonly OutboxHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Enqueue_inserts_a_pending_row_that_is_due_immediately()
    {
        var result = await _harness.EnqueueTemplateAsync();

        result.Duplicate.ShouldBeFalse();
        result.Status.ShouldBe(NotificationStatus.Pending);
        var row = _harness.Database.Row(result.Id);
        row.TenantId.ShouldBe("tenant-a");
        row.Recipient.ShouldBe(TestData.Recipient);
        row.EventKey.ShouldBe("booking-status-changed");
        row.ReferenceKey.ShouldBe("bk-1");
        row.Kind.ShouldBe(NotificationKind.Template);
        row.TemplateName.ShouldBe("booking_status_updated");
        row.TemplateLanguage.ShouldBe("en");
        row.TemplateParameters.ShouldBe(["Sam", "REF-1", "Confirmed"]);
        row.Attempts.ShouldBe(0);
        row.NextAttemptAt.ShouldBe(_harness.Time.GetUtcNow());
        row.CreatedAt.ShouldBe(_harness.Time.GetUtcNow());
    }

    [Fact]
    public async Task The_same_key_is_claimed_once_and_the_existing_row_is_returned()
    {
        var first = await _harness.EnqueueTemplateAsync();
        var second = await _harness.EnqueueTemplateAsync();

        second.Duplicate.ShouldBeTrue();
        second.Id.ShouldBe(first.Id);
        _harness.Database.Rows().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_duplicate_reports_the_current_status_of_the_original()
    {
        var first = await _harness.EnqueueTemplateAsync();
        await _harness.CreateProcessor().ProcessBatchAsync(TestContext.Current.CancellationToken);

        var second = await _harness.EnqueueTemplateAsync();

        second.Duplicate.ShouldBeTrue();
        second.Id.ShouldBe(first.Id);
        second.Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task Keys_are_trimmed_before_the_uniqueness_check()
    {
        var first = await _harness.EnqueueTemplateAsync(reference: "bk-1");
        var second = await _harness.EnqueueTemplateAsync(reference: "  bk-1  ");

        second.Duplicate.ShouldBeTrue();
        second.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Long_reference_keys_are_shortened_to_100_characters()
    {
        var result = await _harness.EnqueueTemplateAsync(reference: new string('r', 250));

        _harness.Database.Row(result.Id).ReferenceKey.ShouldBe(new string('r', 100));
        (await _harness.EnqueueTemplateAsync(reference: new string('r', 300))).Duplicate.ShouldBeTrue();
    }

    [Theory]
    [InlineData("tenant", "other-tenant")]
    [InlineData("recipient", "+12025550143")]
    [InlineData("reference", "bk-2")]
    public async Task Any_difference_in_the_key_creates_a_separate_row(string dimension, string value)
    {
        await _harness.EnqueueTemplateAsync();

        var other = dimension switch
        {
            "tenant" => await _harness.EnqueueTemplateAsync(tenantId: value),
            "recipient" => await _harness.EnqueueTemplateAsync(recipient: value),
            _ => await _harness.EnqueueTemplateAsync(reference: value),
        };

        other.Duplicate.ShouldBeFalse();
        _harness.Database.Rows().Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_different_event_key_creates_a_separate_row()
    {
        await _harness.EnqueueTemplateAsync();

        var other = await _harness.Enqueuer.EnqueueAsync(
            new EnqueueNotification("tenant-a", TestData.Recipient, "booking-created", "bk-1", NotificationKind.Template, "booking_status_updated", "en", ["a", "b", "c"]),
            TestContext.Current.CancellationToken);

        other.Duplicate.ShouldBeFalse();
    }

    [Fact]
    public async Task Free_text_keeps_its_text_and_the_optional_fallback_template()
    {
        var result = await _harness.EnqueueFreeTextAsync(fallbackTemplate: TemplateNames.BookingStatusUpdated);

        var row = _harness.Database.Row(result.Id);
        row.Kind.ShouldBe(NotificationKind.FreeText);
        row.Text.ShouldBe("Your table is ready.");
        row.TemplateName.ShouldBe("booking_status_updated");
    }

    [Fact]
    public async Task Many_parallel_enqueues_of_one_key_create_exactly_one_row()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => _harness.EnqueueTemplateAsync()));

        _harness.Database.Rows().ShouldHaveSingleItem();
        results.Count(r => !r.Duplicate).ShouldBe(1);
        results.Select(r => r.Id).Distinct().ShouldHaveSingleItem();
    }

    public static TheoryData<string, EnqueueNotification> InvalidRequests => new()
    {
        { "bad recipient", new("t", "12025550100", "e", "r", NotificationKind.FreeText, Text: "x") },
        { "recipient with spaces", new("t", "+1 202 555 0100", "e", "r", NotificationKind.FreeText, Text: "x") },
        { "empty tenant", new(" ", TestData.Recipient, "e", "r", NotificationKind.FreeText, Text: "x") },
        { "empty event key", new("t", TestData.Recipient, " ", "r", NotificationKind.FreeText, Text: "x") },
        { "empty reference", new("t", TestData.Recipient, "e", "", NotificationKind.FreeText, Text: "x") },
        { "long event key", new("t", TestData.Recipient, new string('e', 101), "r", NotificationKind.FreeText, Text: "x") },
        { "template without name", new("t", TestData.Recipient, "e", "r", NotificationKind.Template) },
        { "free text without text", new("t", TestData.Recipient, "e", "r", NotificationKind.FreeText, Text: " ") },
        { "text too long", new("t", TestData.Recipient, "e", "r", NotificationKind.FreeText, Text: new string('x', 4097)) },
        { "unknown kind", new("t", TestData.Recipient, "e", "r", (NotificationKind)42, Text: "x") },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Malformed_requests_are_rejected_with_a_validation_exception_and_nothing_is_stored(
        string reason,
        EnqueueNotification request)
    {
        await Should.ThrowAsync<NotificationValidationException>(
            () => _harness.Enqueuer.EnqueueAsync(request, TestContext.Current.CancellationToken),
            reason);

        _harness.Database.Rows().ShouldBeEmpty();
    }
}

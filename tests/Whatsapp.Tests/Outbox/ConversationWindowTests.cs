using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whatsapp.Abstractions;
using Whatsapp.Outbox;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Outbox;

public sealed class ConversationWindowTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly SqliteOutboxDatabase _database = new();

    public void Dispose() => _database.Dispose();

    public static TheoryData<string> StoreKinds => ["memory", "database"];

    private IConversationWindowStore CreateStore(string kind)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ConversationWindowOptions());
        return kind == "memory"
            ? new InMemoryConversationWindowStore(_time, options)
            : new EfConversationWindowStore(_database.Factory, _time, options);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task The_window_is_closed_until_an_inbound_message_is_recorded(string kind)
    {
        var store = CreateStore(kind);

        (await store.IsOpenAsync(Tenant, TestData.Recipient, Ct)).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task An_inbound_message_opens_the_window_for_24_hours(string kind)
    {
        var store = CreateStore(kind);
        await store.RecordInboundAsync(Tenant, TestData.Recipient, _time.GetUtcNow(), Ct);

        (await store.IsOpenAsync(Tenant, TestData.Recipient, Ct)).ShouldBeTrue();

        _time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        (await store.IsOpenAsync(Tenant, TestData.Recipient, Ct)).ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(1));
        (await store.IsOpenAsync(Tenant, TestData.Recipient, Ct)).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task The_window_is_per_tenant_and_per_recipient(string kind)
    {
        var store = CreateStore(kind);
        await store.RecordInboundAsync(Tenant, TestData.Recipient, _time.GetUtcNow(), Ct);

        (await store.IsOpenAsync("other-tenant", TestData.Recipient, Ct)).ShouldBeFalse();
        (await store.IsOpenAsync(Tenant, TestData.OtherRecipient, Ct)).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task A_new_inbound_message_extends_the_window_but_an_older_one_never_shortens_it(string kind)
    {
        var store = CreateStore(kind);
        var start = _time.GetUtcNow();
        await store.RecordInboundAsync(Tenant, TestData.Recipient, start, Ct);

        _time.Advance(TimeSpan.FromHours(20));
        await store.RecordInboundAsync(Tenant, TestData.Recipient, _time.GetUtcNow(), Ct);
        await store.RecordInboundAsync(Tenant, TestData.Recipient, start.AddHours(-5), Ct);

        _time.Advance(TimeSpan.FromHours(20));
        (await store.IsOpenAsync(Tenant, TestData.Recipient, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_inbound_messages_leave_the_latest_one_in_the_database()
    {
        var store = CreateStore("database");
        var start = _time.GetUtcNow();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            store.RecordInboundAsync(Tenant, TestData.Recipient, start.AddMinutes(i), Ct)));

        using var db = _database.Factory.CreateDbContext();
        db.ConversationWindows.Single().LastInboundAt.ShouldBe(start.AddMinutes(11));
    }

    // Window policy.

    private readonly IConversationWindowStore _policyStore = Substitute.For<IConversationWindowStore>();

    private void AssertStoreWasNotConsulted() =>
        _policyStore.DidNotReceiveWithAnyArgs().IsOpenAsync(default!, default!, default);

    private Task<WindowDecision> Decide(NotificationKind kind, bool fallback, bool windowOpen, bool enforced = true)
    {
        _policyStore.IsOpenAsync(default!, default!, default).ReturnsForAnyArgs(windowOpen);
        return new ConversationWindowPolicy(_policyStore).DecideAsync(Tenant, TestData.Recipient, kind, fallback, enforced, Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Templates_are_always_sent_as_templates(bool windowOpen)
    {
        (await Decide(NotificationKind.Template, fallback: false, windowOpen)).Kind.ShouldBe(WindowDecisionKind.SendTemplate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Free_text_inside_the_window_is_sent_as_text(bool hasFallback)
    {
        (await Decide(NotificationKind.FreeText, hasFallback, windowOpen: true)).Kind.ShouldBe(WindowDecisionKind.SendFreeText);
    }

    [Fact]
    public async Task Free_text_outside_the_window_falls_back_to_the_template()
    {
        (await Decide(NotificationKind.FreeText, fallback: true, windowOpen: false)).Kind.ShouldBe(WindowDecisionKind.SendTemplate);
    }

    [Fact]
    public async Task Free_text_outside_the_window_without_a_template_is_skipped_as_window_closed()
    {
        var decision = await Decide(NotificationKind.FreeText, fallback: false, windowOpen: false);

        decision.Kind.ShouldBe(WindowDecisionKind.Skip);
        decision.Reason.ShouldBe("window closed");
    }

    [Fact]
    public async Task When_the_tenant_does_not_enforce_the_window_text_is_sent_without_consulting_the_store()
    {
        var decision = await Decide(NotificationKind.FreeText, fallback: true, windowOpen: false, enforced: false);

        decision.Kind.ShouldBe(WindowDecisionKind.SendFreeText);
        AssertStoreWasNotConsulted();
    }
}

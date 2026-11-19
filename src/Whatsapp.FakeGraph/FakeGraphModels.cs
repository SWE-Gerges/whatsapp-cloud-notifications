namespace Whatsapp.FakeGraph;

public sealed class FakeGraphOptions
{
    public const string SectionName = "FakeGraph";

    /// <summary>Bearer tokens answered with an "access token has expired" error.</summary>
    public List<string> ExpiredTokens { get; set; } = [];

    /// <summary>Templates the fake knows. When empty the two built-in booking templates are used.</summary>
    public Dictionary<string, FakeTemplate> Templates { get; set; } = [];

    public int MaxStoredRequests { get; set; } = 1000;

    /// <summary>The <c>Retry-After</c> value (seconds) sent with injected rate-limit errors.</summary>
    public int RetryAfterSeconds { get; set; } = 1;

    /// <summary>Optional: post signed status webhooks for accepted messages and signed inbound messages on request.</summary>
    public FakeWebhookOptions Webhook { get; set; } = new();
}

public sealed class FakeTemplate
{
    public List<string> Languages { get; set; } = ["en", "ar"];

    public int ParameterCount { get; set; }
}

/// <summary>A request the fake received, accepted or not.</summary>
public sealed record ReceivedRequest(
    int Sequence,
    DateTimeOffset ReceivedAt,
    string PhoneNumberId,
    string? To,
    string? Type,
    string? TemplateName,
    string? Language,
    IReadOnlyList<string> Parameters,
    string? Text,
    string Outcome,
    string? MessageId,
    string? CallbackData = null,
    bool ResponseDropped = false);

/// <summary>Body of <c>POST /_inspect/failures</c>: fail the next requests in a given way.</summary>
public sealed record FailureRule(string Mode, int? Remaining = null, string? Recipient = null, int? RetryAfterSeconds = null);

public sealed record FailureRuleView(int Id, string Mode, int? Remaining, string? Recipient, int? RetryAfterSeconds);

public static class FailureModes
{
    public const string RateLimit = "rate-limit";
    public const string ServerError = "server-error";
    public const string ServiceUnavailable = "service-unavailable";
    public const string InvalidRecipient = "invalid-recipient";
    public const string TemplateNotFound = "template-not-found";
    public const string TokenExpired = "token-expired";
    public const string OutsideWindow = "outside-window";
    public const string ParameterMismatch = "parameter-mismatch";

    /// <summary>The message is recorded as accepted and its status webhooks follow, but the connection is dropped before the answer.</summary>
    public const string DropAfterAccept = "drop-after-accept";

    public static readonly IReadOnlyList<string> All =
    [
        RateLimit, ServerError, ServiceUnavailable, InvalidRecipient,
        TemplateNotFound, TokenExpired, OutsideWindow, ParameterMismatch, DropAfterAccept,
    ];
}

/// <summary>Thread-safe log of received requests.</summary>
public sealed class RequestLog(Microsoft.Extensions.Options.IOptions<FakeGraphOptions> options)
{
    private readonly Lock _gate = new();
    private readonly List<ReceivedRequest> _requests = [];
    private int _sequence;

    public ReceivedRequest Add(Func<int, ReceivedRequest> create)
    {
        lock (_gate)
        {
            var request = create(++_sequence);
            _requests.Add(request);
            if (_requests.Count > options.Value.MaxStoredRequests)
            {
                _requests.RemoveAt(0);
            }

            return request;
        }
    }

    public IReadOnlyList<ReceivedRequest> Snapshot()
    {
        lock (_gate)
        {
            return [.. _requests];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _requests.Clear();
        }
    }
}

/// <summary>Failure rules that fire on matching requests until their count is used up.</summary>
public sealed class FailureRules
{
    private readonly Lock _gate = new();
    private readonly List<(int Id, FailureRule Rule, int? Remaining)> _rules = [];
    private int _nextId;

    public FailureRuleView Add(FailureRule rule)
    {
        lock (_gate)
        {
            var id = ++_nextId;
            _rules.Add((id, rule, rule.Remaining));
            return new FailureRuleView(id, rule.Mode, rule.Remaining, rule.Recipient, rule.RetryAfterSeconds);
        }
    }

    public IReadOnlyList<FailureRuleView> List()
    {
        lock (_gate)
        {
            return [.. _rules.Select(r => new FailureRuleView(r.Id, r.Rule.Mode, r.Remaining, r.Rule.Recipient, r.Rule.RetryAfterSeconds))];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    /// <summary>Returns the first rule matching the recipient and consumes one use of it.</summary>
    public FailureRule? Take(string? recipient)
    {
        lock (_gate)
        {
            for (var i = 0; i < _rules.Count; i++)
            {
                var (id, rule, remaining) = _rules[i];
                if (rule.Recipient is not null && !SameNumber(rule.Recipient, recipient))
                {
                    continue;
                }

                if (remaining is { } left)
                {
                    if (left <= 1)
                    {
                        _rules.RemoveAt(i);
                    }
                    else
                    {
                        _rules[i] = (id, rule, left - 1);
                    }
                }

                return rule;
            }

            return null;
        }
    }

    private static bool SameNumber(string a, string? b) =>
        b is not null && Digits(a) == Digits(b);

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
}

internal sealed class WamidGenerator
{
    private long _counter;

    public string NextInbound() => $"wamid.FAKEIN{Interlocked.Increment(ref _counter):D6}{Guid.NewGuid():N}";

    public string Next() =>
        $"wamid.FAKE{Interlocked.Increment(ref _counter):D6}{Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', 'A').Replace('/', 'B')}";
}

/// <summary>Body of <c>POST /_inspect/inbound</c>: the fake posts a signed inbound message webhook for this sender.</summary>
/// <param name="From">The customer number, any format; the webhook carries its digits as <c>wa_id</c>.</param>
/// <param name="PhoneNumberId">The business phone number id the customer wrote to. It selects the tenant in the service.</param>
/// <param name="Timestamp">Epoch seconds of the message. Defaults to now.</param>
public sealed record InboundMessageSimulation(string? From, string? PhoneNumberId, string? Text = null, string? Name = null, long? Timestamp = null);

/// <summary>The services one message request needs, resolved once.</summary>
internal sealed record MessageServices(
    RequestLog Log,
    FailureRules Rules,
    WamidGenerator Wamids,
    TimeProvider Time,
    Microsoft.Extensions.Options.IOptions<FakeGraphOptions> Options,
    OutcomeRules Outcomes,
    StatusQueue Queue);

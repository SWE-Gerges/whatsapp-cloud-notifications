using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Whatsapp.FakeGraph;

/// <summary>How far a message the fake accepted gets, as told by the status webhooks it posts.</summary>
public enum FakeOutcome
{
    /// <summary>sent, delivered, read.</summary>
    Read = 0,

    /// <summary>sent, delivered.</summary>
    Delivered = 1,

    /// <summary>sent only.</summary>
    Sent = 2,

    /// <summary>A single failed status with an error.</summary>
    Failed = 3,

    /// <summary>The message is accepted but no status webhook follows.</summary>
    None = 4,
}

public sealed class FakeOutcomeSetting
{
    public FakeOutcome Final { get; set; } = FakeOutcome.Read;

    public int? ErrorCode { get; set; }

    public string? ErrorTitle { get; set; }
}

/// <summary>Where and how the fake posts its webhooks. Without a callback URL and an app secret it posts nothing.</summary>
public sealed class FakeWebhookOptions
{
    /// <summary>The webhook endpoint of the service under test.</summary>
    public string? CallbackUrl { get; set; }

    /// <summary>The secret the status webhooks are signed with. It must equal the app secret configured for the tenant.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Time after acceptance at which each status is posted.</summary>
    public TimeSpan SentDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan DeliveredDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan ReadDelay { get; set; } = TimeSpan.FromSeconds(3);

    public string DisplayPhoneNumber { get; set; } = "15555550100";

    /// <summary>Outcome per recipient number (any format; compared by digits). Recipients without an entry are read.</summary>
    public Dictionary<string, FakeOutcomeSetting> Outcomes { get; set; } = [];

    public bool Enabled => !string.IsNullOrWhiteSpace(CallbackUrl) && !string.IsNullOrWhiteSpace(AppSecret);
}

/// <param name="Remaining">How many accepted messages the rule applies to. Without it, to every message for the recipient.</param>
public sealed record OutcomeRule(string Recipient, FakeOutcome Final, int? ErrorCode = null, string? ErrorTitle = null, int? Remaining = null);

/// <summary>Runtime outcome rules, registered through <c>POST /_inspect/outcomes</c>; they win over the configured ones.</summary>
public sealed class OutcomeRules(IOptions<FakeGraphOptions> options)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, OutcomeRule> _rules = new(StringComparer.Ordinal);

    public OutcomeRule Add(OutcomeRule rule)
    {
        lock (_gate)
        {
            _rules[Digits(rule.Recipient)] = rule;
            return rule;
        }
    }

    public IReadOnlyList<OutcomeRule> List()
    {
        lock (_gate)
        {
            return [.. _rules.Values];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    /// <summary>The outcome for the next accepted message to the recipient. A rule with a count is used up by it.</summary>
    public OutcomeRule Resolve(string? recipient)
    {
        var digits = Digits(recipient ?? string.Empty);
        lock (_gate)
        {
            if (_rules.TryGetValue(digits, out var rule))
            {
                if (rule.Remaining is { } left)
                {
                    if (left <= 1)
                    {
                        _rules.Remove(digits);
                    }
                    else
                    {
                        _rules[digits] = rule with { Remaining = left - 1 };
                    }
                }

                return rule;
            }
        }

        foreach (var (number, setting) in options.Value.Webhook.Outcomes)
        {
            if (Digits(number) == digits)
            {
                return new OutcomeRule(number, setting.Final, setting.ErrorCode, setting.ErrorTitle);
            }
        }

        return new OutcomeRule(recipient ?? string.Empty, FakeOutcome.Read);
    }

    internal static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
}

/// <summary>One webhook the fake posted (or tried to).</summary>
public sealed record WebhookDelivery(
    int Sequence,
    DateTimeOffset At,
    string Kind,
    string Status,
    string? MessageId,
    string? CallbackData,
    int? HttpStatus,
    string? Error);

public sealed class WebhookLog(IOptions<FakeGraphOptions> options)
{
    private readonly Lock _gate = new();
    private readonly List<WebhookDelivery> _items = [];
    private int _sequence;

    public WebhookDelivery Add(Func<int, WebhookDelivery> create)
    {
        lock (_gate)
        {
            var item = create(++_sequence);
            _items.Add(item);
            if (_items.Count > options.Value.MaxStoredRequests)
            {
                _items.RemoveAt(0);
            }

            return item;
        }
    }

    public IReadOnlyList<WebhookDelivery> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}

/// <summary>An accepted message whose status webhooks are still to be posted.</summary>
internal sealed record AcceptedMessage(
    string PhoneNumberId,
    string MessageId,
    string RecipientWaId,
    string? CallbackData,
    OutcomeRule Outcome);

internal sealed class StatusQueue
{
    private readonly Channel<AcceptedMessage> _channel = Channel.CreateUnbounded<AcceptedMessage>();

    public ChannelReader<AcceptedMessage> Reader => _channel.Reader;

    public void Enqueue(AcceptedMessage message) => _channel.Writer.TryWrite(message);
}

/// <summary>Builds the webhook bodies. The shapes follow the provider's documented payloads.</summary>
internal static class WebhookPayloads
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Status(
        FakeWebhookOptions options,
        string phoneNumberId,
        string messageId,
        string recipientWaId,
        string status,
        long timestamp,
        string? callbackData,
        OutcomeRule outcome)
    {
        return Envelope(options, phoneNumberId, value =>
        {
            value.WriteStartArray("statuses");
            value.WriteStartObject();
            value.WriteString("id", messageId);
            value.WriteString("status", status);
            value.WriteString("timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            value.WriteString("recipient_id", recipientWaId);
            if (!string.IsNullOrEmpty(callbackData))
            {
                value.WriteString("biz_opaque_callback_data", callbackData);
            }

            if (status == "failed")
            {
                var title = outcome.ErrorTitle ?? "Message undeliverable";
                value.WriteStartArray("errors");
                value.WriteStartObject();
                value.WriteNumber("code", outcome.ErrorCode ?? 131026);
                value.WriteString("title", title);
                value.WriteString("message", title);
                value.WriteStartObject("error_data");
                value.WriteString("details", title);
                value.WriteEndObject();
                value.WriteEndObject();
                value.WriteEndArray();
            }

            value.WriteEndObject();
            value.WriteEndArray();
        });
    }

    public static byte[] Inbound(
        FakeWebhookOptions options,
        string phoneNumberId,
        string messageId,
        string fromWaId,
        string? name,
        string text,
        long timestamp)
    {
        return Envelope(options, phoneNumberId, value =>
        {
            value.WriteStartArray("contacts");
            value.WriteStartObject();
            value.WriteStartObject("profile");
            value.WriteString("name", name ?? "Demo customer");
            value.WriteEndObject();
            value.WriteString("wa_id", fromWaId);
            value.WriteEndObject();
            value.WriteEndArray();

            value.WriteStartArray("messages");
            value.WriteStartObject();
            value.WriteString("from", fromWaId);
            value.WriteString("id", messageId);
            value.WriteString("timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            value.WriteStartObject("text");
            value.WriteString("body", text);
            value.WriteEndObject();
            value.WriteString("type", "text");
            value.WriteEndObject();
            value.WriteEndArray();
        });
    }

    private static byte[] Envelope(FakeWebhookOptions options, string phoneNumberId, Action<Utf8JsonWriter> writeValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("object", "whatsapp_business_account");
            writer.WriteStartArray("entry");
            writer.WriteStartObject();
            writer.WriteString("id", "109876543210");
            writer.WriteStartArray("changes");
            writer.WriteStartObject();
            writer.WriteStartObject("value");
            writer.WriteString("messaging_product", "whatsapp");
            writer.WriteStartObject("metadata");
            writer.WriteString("display_phone_number", options.DisplayPhoneNumber);
            writer.WriteString("phone_number_id", phoneNumberId);
            writer.WriteEndObject();
            writeValue(writer);
            writer.WriteEndObject();
            writer.WriteString("field", "messages");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

/// <summary>Signs and posts one webhook, and records the result. Failures are returned and logged, never thrown.</summary>
public sealed class WebhookSender(
    IHttpClientFactory httpClients,
    IOptions<FakeGraphOptions> options,
    WebhookLog log,
    TimeProvider time,
    ILogger<WebhookSender> logger)
{
    public const string ClientName = "fake-graph-webhook";
    public const string SignatureHeader = "X-Hub-Signature-256";

    public bool Enabled => options.Value.Webhook.Enabled;

    internal static string Sign(string appSecret, ReadOnlySpan<byte> body)
    {
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body, digest);
        return "sha256=" + Convert.ToHexStringLower(digest);
    }

    internal async Task<WebhookDelivery> PostAsync(
        string kind,
        string status,
        string? messageId,
        string? callbackData,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.Webhook;
        int? httpStatus = null;
        string? error = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.CallbackUrl)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(settings.AppSecret!, body));

            using var response = await httpClients.CreateClient(ClientName).SendAsync(request, cancellationToken);
            httpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                error = $"The callback answered HTTP {httpStatus}.";
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning("The {Kind} webhook ({Status}) could not be posted: {Error}", kind, status, error);
        }

        return log.Add(sequence => new WebhookDelivery(sequence, time.GetUtcNow(), kind, status, messageId, callbackData, httpStatus, error));
    }
}

/// <summary>Posts the status webhooks of accepted messages, in order, after the configured delays.</summary>
internal sealed class StatusWebhookService(
    StatusQueue queue,
    WebhookSender sender,
    IOptions<FakeGraphOptions> options,
    TimeProvider time,
    ILogger<StatusWebhookService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new ConcurrentDictionary<Task, byte>();
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (!sender.Enabled)
                {
                    continue;
                }

                var task = DeliverAsync(message, stoppingToken);
                running.TryAdd(task, 0);
                _ = task.ContinueWith(finished => running.TryRemove(finished, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the deliveries in flight observe the same token.
        }

        await Task.WhenAll(running.Keys);
    }

    private async Task DeliverAsync(AcceptedMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var settings = options.Value.Webhook;
            var elapsed = TimeSpan.Zero;
            foreach (var (status, at) in Plan(settings, message.Outcome))
            {
                if (at > elapsed)
                {
                    await Task.Delay(at - elapsed, time, cancellationToken);
                    elapsed = at;
                }

                var body = WebhookPayloads.Status(
                    settings,
                    message.PhoneNumberId,
                    message.MessageId,
                    message.RecipientWaId,
                    status,
                    time.GetUtcNow().ToUnixTimeSeconds(),
                    message.CallbackData,
                    message.Outcome);
                await sender.PostAsync("status", status, message.MessageId, message.CallbackData, body, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // A fake must not take the host down, but a silent failure would make a demo look broken for no visible reason.
            logger.LogError(ex, "Posting the status webhooks of message {MessageId} failed", message.MessageId);
        }
    }

    internal static IReadOnlyList<(string Status, TimeSpan At)> Plan(FakeWebhookOptions settings, OutcomeRule outcome) => outcome.Final switch
    {
        FakeOutcome.Read =>
        [
            ("sent", settings.SentDelay),
            ("delivered", settings.DeliveredDelay),
            ("read", settings.ReadDelay),
        ],
        FakeOutcome.Delivered => [("sent", settings.SentDelay), ("delivered", settings.DeliveredDelay)],
        FakeOutcome.Sent => [("sent", settings.SentDelay)],
        FakeOutcome.Failed => [("failed", settings.DeliveredDelay)],
        _ => [],
    };
}

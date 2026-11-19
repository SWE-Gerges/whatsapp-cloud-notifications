using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Whatsapp.FakeGraph;

public static partial class FakeGraphExtensions
{
    public const string FailureHeader = "X-Fake-Failure";

    /// <summary>The longest <c>biz_opaque_callback_data</c> the real endpoint accepts.</summary>
    private const int MaxCallbackDataLength = 512;

    private static readonly IReadOnlyDictionary<string, FakeTemplate> BuiltInTemplates = new Dictionary<string, FakeTemplate>
    {
        ["booking_created"] = new() { Languages = ["en", "ar"], ParameterCount = 5 },
        ["booking_status_updated"] = new() { Languages = ["en", "ar"], ParameterCount = 3 },
    };

    public static IServiceCollection AddFakeGraph(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FakeGraphOptions>().Bind(configuration.GetSection(FakeGraphOptions.SectionName));
        services.AddSingleton<RequestLog>();
        services.AddSingleton<FailureRules>();
        services.AddSingleton<WamidGenerator>();
        services.AddSingleton<OutcomeRules>();
        services.AddSingleton<WebhookLog>();
        services.AddSingleton<StatusQueue>();
        services.AddSingleton<WebhookSender>();
        services.AddSingleton<MessageServices>();
        services.AddSingleton(TimeProvider.System);
        services.AddHostedService<StatusWebhookService>();
        services.AddHttpClient(WebhookSender.ClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        return services;
    }

    public static void MapFakeGraph(this WebApplication app)
    {
        app.MapGet("/", () => Results.Ok(new { service = "fake-graph", inspect = "/_inspect/messages" }));

        app.MapPost("/{version}/{phoneNumberId}/messages", HandleMessageAsync);
        app.MapPost("/{phoneNumberId}/messages", (HttpContext context, string phoneNumberId, MessageServices services)
            => HandleMessageAsync(context, null, phoneNumberId, services));

        var inspect = app.MapGroup("/_inspect");
        inspect.MapGet("/messages", (RequestLog log, string? to) => Results.Ok(
            log.Snapshot()
                .Where(r => r.Outcome == "accepted" && (to is null || SameNumber(r.To, to)))
                .ToList()));
        inspect.MapGet("/requests", (RequestLog log) => Results.Ok(log.Snapshot()));
        inspect.MapDelete("/messages", (RequestLog log, WebhookLog webhooks) =>
        {
            log.Clear();
            webhooks.Clear();
            return Results.NoContent();
        });
        inspect.MapGet("/failures", (FailureRules rules) => Results.Ok(rules.List()));
        inspect.MapPost("/failures", (FailureRule rule, FailureRules rules) =>
            FailureModes.All.Contains(rule.Mode)
                ? Results.Created("/_inspect/failures", rules.Add(rule))
                : Results.BadRequest(new { error = $"Unknown mode '{rule.Mode}'.", modes = FailureModes.All }));
        inspect.MapDelete("/failures", (FailureRules rules) =>
        {
            rules.Clear();
            return Results.NoContent();
        });

        inspect.MapGet("/webhooks", (WebhookLog webhooks) => Results.Ok(webhooks.Snapshot()));
        inspect.MapGet("/outcomes", (OutcomeRules outcomes) => Results.Ok(outcomes.List()));
        inspect.MapPost("/outcomes", (OutcomeRule rule, OutcomeRules outcomes) =>
            string.IsNullOrWhiteSpace(rule.Recipient) || OutcomeRules.Digits(rule.Recipient).Length < 8
                ? Results.BadRequest(new { error = "recipient must be a phone number." })
                : Results.Created("/_inspect/outcomes", outcomes.Add(rule)));
        inspect.MapDelete("/outcomes", (OutcomeRules outcomes) =>
        {
            outcomes.Clear();
            return Results.NoContent();
        });
        inspect.MapPost("/inbound", PostInboundAsync);
    }

    /// <summary>Demo: the fake posts a signed inbound message webhook, so the 24 hour window of the service opens through its real webhook route.</summary>
    private static async Task<IResult> PostInboundAsync(
        InboundMessageSimulation request,
        WebhookSender sender,
        IOptions<FakeGraphOptions> options,
        TimeProvider time,
        WamidGenerator wamids,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.Webhook;
        if (!sender.Enabled)
        {
            return Results.Json(
                new { error = "Webhook delivery is not configured: set FakeGraph:Webhook:CallbackUrl and FakeGraph:Webhook:AppSecret." },
                statusCode: StatusCodes.Status409Conflict);
        }

        if (string.IsNullOrWhiteSpace(request.PhoneNumberId))
        {
            return Results.BadRequest(new { error = "phoneNumberId is required: it selects the tenant that owns the number." });
        }

        var from = OutcomeRules.Digits(request.From ?? string.Empty);
        if (from.Length is < 8 or > 15)
        {
            return Results.BadRequest(new { error = "from must be a phone number." });
        }

        var messageId = wamids.NextInbound();
        var body = WebhookPayloads.Inbound(
            settings,
            request.PhoneNumberId,
            messageId,
            from,
            request.Name,
            request.Text ?? "Hello",
            request.Timestamp ?? time.GetUtcNow().ToUnixTimeSeconds());

        var delivery = await sender.PostAsync("inbound", "message", messageId, null, body, cancellationToken);
        return delivery.Error is null
            ? Results.Ok(delivery)
            : Results.Json(delivery, statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> HandleMessageAsync(
        HttpContext context,
        string? version,
        string phoneNumberId,
        MessageServices services)
    {
        var (log, rules, wamids, time, options, outcomes, queue) = services;
        var settings = options.Value;

        using var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        var payload = Inspect(body);

        IResult Respond(string outcome, IResult result, string? messageId = null, bool dropped = false)
        {
            log.Add(sequence => new ReceivedRequest(
                sequence, time.GetUtcNow(), phoneNumberId, payload.To, payload.Type, payload.TemplateName,
                payload.Language, payload.Parameters, payload.Text, outcome, messageId, payload.CallbackData, dropped));
            return result;
        }

        // 1. Authentication.
        var token = ReadBearer(context.Request);
        if (token is null)
        {
            return Respond("rejected: missing token", Error(401, 190, "Invalid OAuth access token - Cannot parse access token", subcode: 2));
        }

        if (settings.ExpiredTokens.Contains(token, StringComparer.Ordinal))
        {
            return Respond(FailureModes.TokenExpired, Error(401, 190, "Error validating access token: Session has expired", subcode: 463));
        }

        // 2. Injected failure (header first, then registered rules). A dropped answer is not a failure of the message
        //    itself: the message is validated and accepted below, and only the answer is lost.
        var mode = context.Request.Headers.TryGetValue(FailureHeader, out var headerMode) ? headerMode.ToString() : null;
        FailureRule? rule = null;
        if (string.IsNullOrEmpty(mode))
        {
            rule = rules.Take(payload.To);
            mode = rule?.Mode;
        }

        var dropAfterAccept = mode == FailureModes.DropAfterAccept;
        if (!string.IsNullOrEmpty(mode) && !dropAfterAccept)
        {
            return Respond(mode, FailureResponse(mode, context, rule?.RetryAfterSeconds ?? settings.RetryAfterSeconds));
        }

        // 3. Payload validation, like the real endpoint.
        var invalid = Validate(payload, settings);
        if (invalid is not null)
        {
            return Respond($"rejected: {invalid.Value.Reason}", invalid.Value.Result);
        }

        var messageId = wamids.Next();
        var waId = Digits(payload.To!);
        queue.Enqueue(new AcceptedMessage(phoneNumberId, messageId, waId, payload.CallbackData, outcomes.Resolve(payload.To)));

        if (dropAfterAccept)
        {
            // The message is recorded and its webhooks are on the way; the caller only sees the connection break.
            Respond("accepted", Results.Empty, messageId, dropped: true);
            context.Abort();
            return Results.Empty;
        }

        return Respond(
            "accepted",
            Results.Json(new
            {
                messaging_product = "whatsapp",
                contacts = new[] { new { input = payload.To, wa_id = waId } },
                messages = new[] { new { id = messageId } },
            }),
            messageId);
    }

    private static (string Reason, IResult Result)? Validate(PayloadView payload, FakeGraphOptions settings)
    {
        if (payload.Malformed)
        {
            return ("malformed json", Error(400, 100, "Invalid parameter: the request body is not valid JSON."));
        }

        if (payload.MessagingProduct != "whatsapp")
        {
            return ("messaging_product", Error(400, 100, "Invalid parameter: messaging_product must be 'whatsapp'."));
        }

        if (payload.To is null || !PhoneLike().IsMatch(payload.To))
        {
            return ("to", Error(400, 100, "Invalid parameter: 'to' is not a valid phone number."));
        }

        if (payload.CallbackData is { Length: > MaxCallbackDataLength })
        {
            return ("biz_opaque_callback_data", Error(400, 100, $"Invalid parameter: biz_opaque_callback_data must not exceed {MaxCallbackDataLength} characters."));
        }

        switch (payload.Type)
        {
            case "text":
                return string.IsNullOrWhiteSpace(payload.Text)
                    ? ("text.body", Error(400, 100, "Invalid parameter: text.body is required."))
                    : null;

            case "template":
                return ValidateTemplate(payload, settings);

            default:
                return ("type", Error(400, 100, "Invalid parameter: type must be 'text' or 'template'."));
        }
    }

    private static (string Reason, IResult Result)? ValidateTemplate(PayloadView payload, FakeGraphOptions settings)
    {
        var templates = settings.Templates.Count > 0 ? settings.Templates : BuiltInTemplates;
        if (payload.TemplateName is null
            || !templates.TryGetValue(payload.TemplateName, out var template)
            || payload.Language is null
            || !template.Languages.Contains(payload.Language, StringComparer.OrdinalIgnoreCase))
        {
            return ("template not found", Error(404, 132001, "Template name does not exist in the translation", detail: "template not found"));
        }

        if (payload.Parameters.Count != template.ParameterCount)
        {
            return ("parameter count", Error(400, 132000, "Number of parameters does not match the expected number of params"));
        }

        if (payload.Parameters.Any(p => string.IsNullOrWhiteSpace(p) || NewlineTabOrSpaces().IsMatch(p)))
        {
            return ("parameter format", Error(400, 132018, "There's an issue with the parameters in your template: parameter text has new-line/tab characters or too many consecutive spaces"));
        }

        return null;
    }

    private static IResult FailureResponse(string mode, HttpContext context, int retryAfterSeconds)
    {
        switch (mode)
        {
            case FailureModes.RateLimit:
                context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Error(429, 130429, "Rate limit hit");
            case FailureModes.ServerError:
                return Error(500, 2, "An unexpected error has occurred. Please retry your request later.", transient: true);
            case FailureModes.ServiceUnavailable:
                return Error(503, 131016, "Service unavailable", transient: true);
            case FailureModes.InvalidRecipient:
                return Error(400, 131026, "Message undeliverable");
            case FailureModes.TemplateNotFound:
                return Error(404, 132001, "Template name does not exist in the translation");
            case FailureModes.TokenExpired:
                return Error(401, 190, "Error validating access token: Session has expired", subcode: 463);
            case FailureModes.OutsideWindow:
                return Error(400, 131047, "Re-engagement message: more than 24 hours have passed since the customer last replied to this number");
            case FailureModes.ParameterMismatch:
                return Error(400, 132000, "Number of parameters does not match the expected number of params");
            default:
                return Error(400, 100, $"Unknown failure mode '{mode}'.");
        }
    }

    private static IResult Error(int status, int code, string message, int? subcode = null, string? detail = null, bool transient = false) =>
        Results.Json(
            new
            {
                error = new
                {
                    message = $"(#{code}) {message}",
                    type = "OAuthException",
                    is_transient = transient ? (bool?)true : null,
                    code,
                    error_subcode = subcode,
                    error_data = detail is null ? null : new { messaging_product = "whatsapp", details = detail },
                    fbtrace_id = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', 'A').Replace('/', 'B'),
                },
            },
            statusCode: status);

    private static string? ReadBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static async Task<JsonDocument?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PayloadView Inspect(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return PayloadView.Invalid;
        }

        var root = document.RootElement;
        var view = new PayloadView
        {
            MessagingProduct = String(root, "messaging_product"),
            To = String(root, "to"),
            Type = String(root, "type"),
            CallbackData = String(root, "biz_opaque_callback_data"),
        };

        if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object)
        {
            view.Text = String(text, "body");
        }

        if (root.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.Object)
        {
            view.TemplateName = String(template, "name");
            if (template.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.Object)
            {
                view.Language = String(language, "code");
            }

            if (template.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
            {
                foreach (var component in components.EnumerateArray())
                {
                    if (String(component, "type") != "body"
                        || !component.TryGetProperty("parameters", out var parameters)
                        || parameters.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    view.Parameters.AddRange(parameters.EnumerateArray().Select(p => String(p, "text") ?? string.Empty));
                }
            }
        }

        return view;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool SameNumber(string? a, string? b) =>
        a is not null && b is not null && Digits(a) == Digits(b);

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    private sealed class PayloadView
    {
        public static PayloadView Invalid => new() { Malformed = true };

        public bool Malformed { get; init; }

        public string? MessagingProduct { get; init; }

        public string? To { get; init; }

        public string? Type { get; init; }

        public string? CallbackData { get; init; }

        public string? Text { get; set; }

        public string? TemplateName { get; set; }

        public string? Language { get; set; }

        public List<string> Parameters { get; } = [];
    }

    [GeneratedRegex(@"^\+?\d{8,15}$")]
    private static partial Regex PhoneLike();

    [GeneratedRegex(@"[\r\n\t]| {4,}")]
    private static partial Regex NewlineTabOrSpaces();
}

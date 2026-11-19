using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Whatsapp.Abstractions;

namespace Whatsapp.CloudApi;

/// <summary>
/// Typed HTTP client for the Cloud API. It keeps no per-tenant state: the base URL, phone number id and token
/// arrive with every call, and the Authorization header is set on the request, not on the shared client.
/// </summary>
internal sealed class WhatsappCloudClient : IWhatsappClient
{
    private const int MaxLoggedMessageLength = 300;

    private readonly HttpClient _http;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WhatsappCloudClient> _logger;
    private readonly WhatsappClientOptions _options;
    private readonly ResiliencePipeline _pipeline;

    public WhatsappCloudClient(
        HttpClient http,
        IOptions<WhatsappClientOptions> options,
        TimeProvider timeProvider,
        ILogger<WhatsappCloudClient> logger)
    {
        _http = http;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
        _pipeline = BuildPipeline(_options, timeProvider);
    }

    public async Task<WhatsappSendResult> SendAsync(
        WhatsappMessage message,
        WhatsappTenantOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.AccessToken))
        {
            throw new WhatsappApiException(
                WhatsappErrorKind.Authentication,
                $"No access token is configured for tenant '{options.TenantId}'.");
        }

        var payload = GraphPayloadBuilder.Build(message);
        var requestUri = BuildUri(options);

        var result = await _pipeline.ExecuteAsync(
            async token => await SendOnceAsync(requestUri, payload, options, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Cloud API accepted message {MessageId} for {Recipient} (tenant {TenantId})",
            result.MessageId,
            SensitiveData.MaskPhone(message.To),
            options.TenantId);
        return result;
    }

    private static Uri BuildUri(WhatsappTenantOptions options) =>
        new($"{options.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(options.PhoneNumberId)}/messages");

    private static ResiliencePipeline BuildPipeline(WhatsappClientOptions options, TimeProvider timeProvider)
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = timeProvider };
        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<WhatsappApiException>(ShouldRetryInline(options)),
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.RetryBaseDelay,
                UseJitter = true,
                DelayGenerator = static args => new ValueTask<TimeSpan?>(
                    args.Outcome.Exception is WhatsappApiException { RetryAfter: { } retryAfter }
                        ? retryAfter
                        : null),
            });
        }

        return builder.Build();
    }

    // A failure that may have reached the provider is never retried here: the retry could deliver the message twice.
    // The caller decides what to do with an unknown outcome.
    private static Func<WhatsappApiException, bool> ShouldRetryInline(WhatsappClientOptions options) =>
        exception => exception.IsTransient
            && !exception.OutcomeUnknown
            && (exception.RetryAfter is null || exception.RetryAfter <= options.MaxInlineRetryDelay);

    private async ValueTask<WhatsappSendResult> SendOnceAsync(
        Uri requestUri,
        string payload,
        WhatsappTenantOptions options,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);

        using var timeout = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return GraphResponseParser.ParseSuccess(body, response.StatusCode);
            }

            throw CreateException(response, body, options);
        }
        catch (HttpRequestException ex)
        {
            var provablyNotSent = ProvablyNeverLeft(ex);
            throw new WhatsappApiException(
                WhatsappErrorKind.Network,
                provablyNotSent
                    ? "The Cloud API could not be reached."
                    : "The connection to the Cloud API failed after the request may have been written.",
                innerException: ex,
                outcomeUnknown: !provablyNotSent);
        }
        catch (IOException ex)
        {
            // A broken stream can only happen once a connection exists, so the request may have been written.
            throw new WhatsappApiException(
                WhatsappErrorKind.Network,
                "The connection to the Cloud API failed after the request may have been written.",
                innerException: ex,
                outcomeUnknown: true);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Either the per-attempt timeout fired, or the transport cancelled the call itself (an in-process server that
            // aborts the connection does this). Neither proves that the request did not leave.
            var timedOut = timeout.IsCancellationRequested;
            throw new WhatsappApiException(
                timedOut ? WhatsappErrorKind.Timeout : WhatsappErrorKind.Network,
                timedOut
                    ? $"The Cloud API did not answer within {_options.RequestTimeout.TotalSeconds:0.##} seconds."
                    : "The connection to the Cloud API was aborted after the request may have been written.",
                innerException: ex,
                outcomeUnknown: true);
        }
    }

    /// <summary>
    /// Failures that can only happen before the request is sent: the name did not resolve, no connection or TLS session
    /// could be set up, or the proxy refused the tunnel. Anything else, including an unknown error, is treated as possibly
    /// sent, because retrying a message that was in fact accepted delivers it twice.
    /// </summary>
    private static bool ProvablyNeverLeft(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError
            or HttpRequestError.VersionNegotiationError;

    private WhatsappApiException CreateException(HttpResponseMessage response, string body, WhatsappTenantOptions options)
    {
        var error = GraphResponseParser.ParseError(body);
        var kind = WhatsappErrorClassifier.Classify(response.StatusCode, error);
        var retryAfter = ParseRetryAfter(response);

        var description = error?.Message is { Length: > 0 } graphMessage
            ? SensitiveData.MaskPhonesInText(graphMessage)
            : $"HTTP {(int)response.StatusCode}";
        if (description.Length > MaxLoggedMessageLength)
        {
            description = description[..MaxLoggedMessageLength];
        }

        _logger.LogWarning(
            "Cloud API rejected a message for tenant {TenantId}: {Kind} (HTTP {Status}, code {Code}, subcode {Subcode}, trace {Trace}, transient {Transient})",
            options.TenantId,
            kind,
            (int)response.StatusCode,
            error?.Code,
            error?.Subcode,
            error?.FbTraceId,
            kind.IsTransient());

        return new WhatsappApiException(kind, description, response.StatusCode, error, retryAfter);
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}

using System.Text.RegularExpressions;

namespace Whatsapp.Api.Infrastructure;

/// <summary>
/// Accepts an <c>X-Correlation-Id</c> from the caller (or generates one), echoes it on the response and adds it to
/// the logging scope of the request. Values that are not simple identifiers are replaced, so a caller cannot
/// inject arbitrary text into logs or response headers.
/// </summary>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied)
            && supplied.Count == 1
            && supplied[0] is { } value
            && ValidId().IsMatch(value)
                ? value
                : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,64}$")]
    private static partial Regex ValidId();
}

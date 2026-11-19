using System.Net;
using System.Text;

namespace Whatsapp.Tests.Support;

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string Body,
    string? MediaType,
    DateTimeOffset At);

/// <summary>Scripted HTTP handler: responses are consumed in order, the last one repeats.</summary>
internal sealed class StubHttpMessageHandler(TimeProvider time) : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private readonly List<RecordedRequest> _requests = [];
    private Func<CancellationToken, Task<HttpResponseMessage>>? _last;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public StubHttpMessageHandler Respond(
        HttpStatusCode status,
        string body,
        Action<HttpResponseMessage>? configure = null) =>
        Add(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });

    public StubHttpMessageHandler Throw(Exception exception) => Add(_ => Task.FromException<HttpResponseMessage>(exception));

    public StubHttpMessageHandler Hang() => Add(async token =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Func<CancellationToken, Task<HttpResponseMessage>> next;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                body,
                request.Content?.Headers.ContentType?.MediaType,
                time.GetUtcNow()));

            if (_script.Count > 0)
            {
                _last = _script.Dequeue();
            }

            next = _last ?? throw new InvalidOperationException("No response scripted for the stub handler.");
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }

    private StubHttpMessageHandler Add(Func<CancellationToken, Task<HttpResponseMessage>> responder)
    {
        lock (_gate)
        {
            _script.Enqueue(responder);
        }

        return this;
    }
}

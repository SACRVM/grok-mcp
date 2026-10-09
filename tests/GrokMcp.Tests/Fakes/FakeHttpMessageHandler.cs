using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace GrokMcp.Tests.Fakes;

// Test double for HttpClient that lets each test enqueue a sequence of responses
// and inspect what requests were made.
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responders = new();

    public List<Recorded> Requests { get; } = new();

    public sealed record Recorded(HttpMethod Method, Uri Uri, string Body, HttpRequestHeaders Headers);

    public void EnqueueJson(HttpStatusCode status, string body)
    {
        _responders.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }));
    }

    // Answers only after `delay`, honouring cancellation, so a test can trip HttpClient.Timeout
    // or keep a tool busy long enough for its progress heartbeat to fire.
    public void EnqueueDelayedJson(TimeSpan delay, HttpStatusCode status, string body)
    {
        _responders.Enqueue(async (_, ct) =>
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });
    }

    public void EnqueueStatus(HttpStatusCode status, string body = "")
    {
        _responders.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        }));
    }

    public void EnqueueBytes(HttpStatusCode status, byte[] bytes)
    {
        _responders.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(bytes),
        }));
    }

    public void EnqueueException(Exception ex)
    {
        _responders.Enqueue((_, _) => throw ex);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Recorded(request.Method, request.RequestUri!, body, request.Headers));

        if (_responders.Count == 0)
            throw new InvalidOperationException(
                $"FakeHttpMessageHandler: no response queued for {request.Method} {request.RequestUri}");

        return await _responders.Dequeue().Invoke(request, ct);
    }
}

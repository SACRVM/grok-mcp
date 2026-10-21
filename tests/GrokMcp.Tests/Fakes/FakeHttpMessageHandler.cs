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

    // Streams each event as an SSE "data:" line. With `stall` the stream then goes silent without
    // closing, the shape of a dead xAI run; reads honour cancellation so the client's watchdog can end it.
    public void EnqueueSse(IEnumerable<string> events, bool stall = false)
    {
        var text = string.Concat(events.Select(e => $"data: {e}\n\n"));
        _responders.Enqueue((_, _) =>
        {
            var content = new StreamContent(new SseStream(System.Text.Encoding.UTF8.GetBytes(text), stall));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
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

    private sealed class SseStream(byte[] bytes, bool stall) : Stream
    {
        private int _pos;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < bytes.Length)
            {
                var n = Math.Min(buffer.Length, bytes.Length - _pos);
                bytes.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            if (stall) await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

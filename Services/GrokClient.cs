using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GrokMcp.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GrokMcp.Services;

public class GrokClient
{
    // Per-instance and internal-settable so tests can zero-out the wall-clock delays.
    // Production: every GrokClient instance starts with the same [0s, 2s, 6s] schedule.
    internal TimeSpan[] _retryDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
    };

    // Same rationale as _retryDelays: internal-settable so tests can shrink the video poll
    // loop instead of waiting on the real wall clock. Production: poll every 5s, give up after 10min.
    internal TimeSpan _videoPollInterval = TimeSpan.FromSeconds(5);
    internal TimeSpan _videoPollTimeout = TimeSpan.FromMinutes(10);

    // Silence allowed on a streamed chat call before it counts as dead. From GrokOptions;
    // internal-settable so tests can use milliseconds.
    internal TimeSpan _streamIdleTimeout;

    private readonly HttpClient _http;
    private readonly GrokOptions _opts;
    private readonly ILogger<GrokClient> _log;

    public GrokClient(HttpClient http, IOptions<GrokOptions> opts, ILogger<GrokClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _streamIdleTimeout = TimeSpan.FromSeconds(_opts.StreamIdleSeconds);

        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("XAI_API_KEY environment variable is required.");

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("grok-mcp/1.0");
    }

    // .NET opens sockets without TCP keepalive. A non-streamed call that waits minutes for its
    // answer (multi-agent, images) leaves its connection idle that long, and a router or firewall
    // that forgets idle connections leaves it dead without telling either end: the call then hangs
    // until the HTTP timeout. Probes after 30 s of silence keep that state alive and turn a dead
    // path into a socket error within about 90 s, which the retry loop handles.
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 6);
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<ChatResult> ChatAsync(
        IEnumerable<object> messages,
        string? model,
        float temperature,
        string? reasoningEffort,
        string? conversationId,
        CancellationToken ct)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? _opts.ChatModel : model;
        var msgList = messages.ToList();
        var body = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["messages"] = msgList,
            ["temperature"] = temperature,
        };
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning_effort"] = reasoningEffort;

        var url = $"{_opts.ApiBaseUrl.TrimEnd('/')}/chat/completions";
        return await PostChatAsync(url, body, msgList, resolvedModel, conversationId, ct);
    }

    // Multi-agent runs on /responses, not /chat/completions — xAI rejects the multi-agent model
    // there with HTTP 400 "Multi Agent requests are not allowed on chat completions". The wire
    // format differs on both ends: 'input' instead of 'messages', and the answer arrives as
    // output[].content[].text instead of choices[0].message.content.
    public async Task<ChatResult> ResponsesAsync(
        IEnumerable<object> messages,
        string? model,
        float temperature,
        string? reasoningEffort,
        string? conversationId,
        CancellationToken ct)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? _opts.MultiAgentModel : model;
        var msgList = messages.ToList();
        var body = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["input"] = msgList,
            ["temperature"] = temperature,
        };
        // /responses documents the nested form; the flat 'reasoning_effort' is only a fallback
        // xAI reads when 'reasoning' is absent. Neither form rejects an unknown value (the call
        // just runs with some default), so the tool layer must validate before we get here.
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning"] = new { effort = reasoningEffort };

        var url = $"{_opts.ApiBaseUrl.TrimEnd('/')}/responses";
        var promptPreview = TryExtractFirstUserText(msgList);
        var json = await PostWithRetryAsync(url, body, resolvedModel, promptPreview, conversationId, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int promptTokens = -1, completionTokens = -1, totalTokens = -1;
        if (root.TryGetProperty("usage", out var usage))
        {
            // /responses names these differently from /chat/completions.
            promptTokens = usage.TryGetProperty("input_tokens", out var p) ? p.GetInt32() : -1;
            completionTokens = usage.TryGetProperty("output_tokens", out var c) ? c.GetInt32() : -1;
            totalTokens = usage.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : -1;
            _log.LogInformation("Grok [{Model}] tokens — input: {Input}, output: {Output}, total: {Total}",
                resolvedModel, promptTokens, completionTokens, totalTokens);
        }

        var sb = new StringBuilder();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                // Items without a content array are reasoning/tool entries, not the answer.
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                        part.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
                }
            }
        }

        return new ChatResult(sb.ToString(), promptTokens, completionTokens, totalTokens);
    }

    public async Task<string> VisionAsync(
        string prompt,
        IEnumerable<object> imageObjects,
        string? model,
        string detail,
        float temperature,
        CancellationToken ct)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? _opts.CreativeModel : model;
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var img in imageObjects)
        {
            // img is { url = "..." } from ImageInputResolver
            var url = img.GetType().GetProperty("url")!.GetValue(img)!;
            content.Add(new { type = "image_url", image_url = new { url, detail } });
        }
        var messages = new object[] { new { role = "user", content = content } };
        var body = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["messages"] = messages,
            ["temperature"] = temperature,
        };
        var endpoint = $"{_opts.ApiBaseUrl.TrimEnd('/')}/chat/completions";
        var result = await PostChatAsync(endpoint, body, messages, resolvedModel, conversationId: null, ct);
        return result.Content;
    }

    public record ChatResult(string Content, int PromptTokens, int CompletionTokens, int TotalTokens);

    public async Task<List<byte[]>> ImagesAsync(
        string prompt,
        string? model,
        int n,
        string? aspectRatio,
        string? resolution,
        string? quality,
        string responseFormat,
        IReadOnlyList<object>? inputs,
        CancellationToken ct)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model) ? _opts.ImageModel : model;
        var hasInputs = inputs != null && inputs.Count > 0;

        var body = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["prompt"] = prompt,
            ["n"] = n,
            ["response_format"] = responseFormat,
        };
        if (!string.IsNullOrWhiteSpace(aspectRatio))
            body["aspect_ratio"] = aspectRatio;
        if (!string.IsNullOrWhiteSpace(resolution))
            body["resolution"] = resolution;
        // Only grok-imagine-image-2.0 honours this; the 1.0 model accepts and ignores it.
        if (!string.IsNullOrWhiteSpace(quality))
            body["quality"] = quality;

        if (hasInputs)
        {
            if (inputs!.Count == 1)
                body["image"] = inputs[0];
            else
                body["images"] = inputs;
        }

        var endpoint = hasInputs
            ? $"{_opts.ApiBaseUrl.TrimEnd('/')}/images/edits"
            : $"{_opts.ApiBaseUrl.TrimEnd('/')}/images/generations";

        var json = await PostWithRetryAsync(endpoint, body, resolvedModel, prompt, conversationId: null, ct);

        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var results = new List<byte[]>(data.GetArrayLength());

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64Prop))
            {
                results.Add(Convert.FromBase64String(b64Prop.GetString() ?? ""));
            }
            else if (item.TryGetProperty("url", out var urlProp))
            {
                var bytes = await _http.GetByteArrayAsync(urlProp.GetString(), ct);
                results.Add(bytes);
            }
        }

        // xAI reports the model that actually served the request (a retired slug is redirected
        // server-side) and the exact cost; both matter now that quality and resolution change
        // the per-image price. Mirrors the token line every chat call logs.
        var servedModel = doc.RootElement.TryGetProperty("model", out var servedProp)
            ? servedProp.GetString() ?? resolvedModel
            : resolvedModel;
        if (doc.RootElement.TryGetProperty("usage", out var imgUsage)
            && imgUsage.TryGetProperty("cost_in_usd_ticks", out var ticksProp)
            && ticksProp.TryGetInt64(out var ticks))
            _log.LogInformation("Grok image [{Model}] done — {Count} image(s), ${Cost:0.000}",
                servedModel, results.Count, ticks / 1e10);
        else
            _log.LogInformation("Grok image [{Model}] done — {Count} image(s)", servedModel, results.Count);
        return results;
    }

    public record VideoResult(byte[] Bytes, int? DurationSeconds);

    public async Task<VideoResult> VideosAsync(
        string prompt,
        string? model,
        int duration,
        string? aspectRatio,
        string? resolution,
        object? imageInput,
        bool? generateAudio,
        CancellationToken ct)
    {
        // Priority: explicit per-call model → GROK_MCP_VIDEO_MODEL (defaults to
        // grok-imagine-video-1.5) → hard fallback for an empty option. One model serves both
        // text-to-video and image-to-video since xAI added text-to-video to 1.5 on 2026-07-31
        // (live-verified 2026-09-07); the older grok-imagine-video has no 1080p.
        var resolvedModel = !string.IsNullOrWhiteSpace(model) ? model
            : !string.IsNullOrWhiteSpace(_opts.VideoModel) ? _opts.VideoModel
            : "grok-imagine-video-1.5";

        var body = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["prompt"] = prompt,
            ["duration"] = duration,
        };
        if (!string.IsNullOrWhiteSpace(aspectRatio))
            body["aspect_ratio"] = aspectRatio;
        if (!string.IsNullOrWhiteSpace(resolution))
            body["resolution"] = resolution;
        if (imageInput != null)
            body["image"] = imageInput;
        if (generateAudio is { } audio)
            body["generate_audio"] = audio;

        var endpoint = $"{_opts.ApiBaseUrl.TrimEnd('/')}/videos/generations";
        var json = await PostWithRetryAsync(endpoint, body, resolvedModel, prompt, conversationId: null, ct);

        using var startDoc = JsonDocument.Parse(json);
        var requestId = startDoc.RootElement.TryGetProperty("request_id", out var reqIdProp)
            ? reqIdProp.GetString()
            : null;
        if (string.IsNullOrEmpty(requestId))
            throw new InvalidOperationException("Grok video request did not return a request_id.");

        var pollUrl = $"{_opts.ApiBaseUrl.TrimEnd('/')}/videos/{requestId}";
        var deadline = DateTime.UtcNow + _videoPollTimeout;

        while (true)
        {
            await Task.Delay(_videoPollInterval, ct);

            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"Grok video generation timed out after {_videoPollTimeout.TotalMinutes:0} minutes (request_id={requestId}).");

            string pollBody;
            try
            {
                pollBody = await GetWithAuthAsync(pollUrl, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                // Individual poll failures aren't fatal — xAI's own poll example just retries
                // every 5s until done/failed/expired or our own timeout is hit.
                _log.LogWarning("Grok video poll failure (request_id={RequestId}): {Err}", requestId, ex.Message);
                continue;
            }

            using var pollDoc = JsonDocument.Parse(pollBody);
            var status = pollDoc.RootElement.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString() ?? ""
                : "";

            switch (status)
            {
                case "done":
                    var video = pollDoc.RootElement.GetProperty("video");
                    var videoUrl = video.GetProperty("url").GetString();
                    if (string.IsNullOrEmpty(videoUrl))
                        throw new InvalidOperationException(
                            $"Grok video (request_id={requestId}) reported done but has no video.url.");
                    int? durationSeconds = video.TryGetProperty("duration", out var durProp)
                        && durProp.ValueKind == JsonValueKind.Number
                        ? durProp.GetInt32()
                        : null;
                    _log.LogInformation(
                        "Grok video [{Model}] done — request_id={RequestId}, duration={Duration}s",
                        resolvedModel, requestId, durationSeconds);
                    var bytes = await _http.GetByteArrayAsync(videoUrl, ct);
                    return new VideoResult(bytes, durationSeconds);

                case "failed":
                case "expired":
                    var detail = pollDoc.RootElement.TryGetProperty("error", out var errProp)
                        ? errProp.ToString()
                        : pollBody;
                    throw new InvalidOperationException(
                        $"Grok video generation {status} (request_id={requestId}). Detail: {detail}");

                default: // "pending" or any other in-progress status — keep polling
                    continue;
            }
        }
    }

    private async Task<string> GetWithAuthAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
        var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Grok video poll HTTP {(int)response.StatusCode}: {body}");
        return body;
    }

    // Chat completions always stream. The caller still gets one answer at the end, but the stream
    // is how a dead run shows: a reasoning model sends reasoning_content chunks every few seconds
    // while it thinks, where a non-streamed call is silent until it is done, dead or alive.
    private async Task<ChatResult> PostChatAsync(
        string endpoint,
        Dictionary<string, object> body,
        IEnumerable<object> messages,
        string model,
        string? conversationId,
        CancellationToken ct)
    {
        body["stream"] = true;
        body["stream_options"] = new { include_usage = true };
        var promptPreview = TryExtractFirstUserText(messages);
        var json = await PostWithRetryAsync(endpoint, body, model, promptPreview, conversationId, ct, stream: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int promptTokens = -1, completionTokens = -1, totalTokens = -1;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : -1;
            completionTokens = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : -1;
            totalTokens = usage.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : -1;
            _log.LogInformation("Grok [{Model}] tokens — prompt: {Prompt}, completion: {Completion}, total: {Total}",
                model, promptTokens, completionTokens, totalTokens);
        }

        var content = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return new ChatResult(content, promptTokens, completionTokens, totalTokens);
    }

    private async Task<string> PostWithRetryAsync(
        string endpoint,
        object body,
        string model,
        string promptPreview,
        string? conversationId,
        CancellationToken ct,
        bool stream = false)
    {
        Exception? lastEx = null;
        var requestJson = JsonSerializer.Serialize(body);
        var preview = promptPreview.Length > 100 ? promptPreview.Substring(0, 100) + "..." : promptPreview;

        for (var attempt = 1; attempt <= _retryDelays.Length; attempt++)
        {
            if (attempt > 1)
                await Task.Delay(_retryDelays[attempt - 1], ct);

            _log.LogDebug("Grok call (attempt {N}/{Max}) → {Endpoint}, model={Model}, payload={Bytes}B, prompt={Preview}",
                attempt, _retryDelays.Length, endpoint, model, requestJson.Length, preview);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
            // Sticky-route to the same xAI server so the prompt-prefix cache hits across turns
            // (cached input is a quarter of normal input on grok-4.5, a sixth on grok-4.3).
            // Header documented in
            // https://docs.x.ai/developers/advanced-api-usage/prompt-caching/maximizing-cache-hits
            if (!string.IsNullOrEmpty(conversationId))
                request.Headers.Add("x-grok-conv-id", conversationId);

            // A streamed call is watched for silence: the watchdog fires when nothing has arrived
            // for _streamIdleTimeout. HttpClient's own timeout stops covering the body once the
            // headers are in, so the overall deadline is a token of ours that the watchdog hangs off.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            if (stream)
            {
                deadline.CancelAfter(_http.Timeout);
                watchdog.CancelAfter(_streamIdleTimeout);
            }

            HttpResponseMessage? response = null;
            string responseBody;
            try
            {
                response = await _http.SendAsync(request,
                    stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                    watchdog.Token);
                responseBody = stream && response.IsSuccessStatusCode &&
                               response.Content.Headers.ContentType?.MediaType == "text/event-stream"
                    ? await ReadChatStreamAsync(response, watchdog)
                    : await response.Content.ReadAsStringAsync(watchdog.Token);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && ex.InnerException is TimeoutException)
            {
                // HttpClient.Timeout elapsed. On a reasoning model that means xAI is still thinking,
                // not that the network blipped: a retry restarts the same run from zero and bills it twice.
                response?.Dispose();
                throw OverallTimeout(model, attempt, ex);
            }
            catch (OperationCanceledException ex) when (stream && !ct.IsCancellationRequested)
            {
                response?.Dispose();
                if (deadline.IsCancellationRequested)
                    throw OverallTimeout(model, attempt, ex);
                // Silence on a stream that should be ticking: the run or the connection is dead, so
                // nothing will ever arrive on it. A retry is the only way to an answer.
                lastEx = new TimeoutException(
                    $"xAI went silent for {_streamIdleTimeout.TotalSeconds:0}s mid-call (GROK_MCP_STREAM_IDLE_SEC).", ex);
                _log.LogWarning("Grok stream silent for {Seconds}s (attempt {N}/{Max}, model={Model})",
                    _streamIdleTimeout.TotalSeconds, attempt, _retryDelays.Length, model);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
            {
                response?.Dispose();
                if (ct.IsCancellationRequested) throw;
                lastEx = ex;
                _log.LogWarning("Grok network failure (attempt {N}/{Max}, model={Model}): {Err}",
                    attempt, _retryDelays.Length, model, ex.Message);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                response.Dispose();
                return responseBody;
            }

            var status = (int)response.StatusCode;
            var transient = status >= 500 || status == 429;
            var bodyPreview = responseBody?.Length > 300 ? responseBody.Substring(0, 300) + "..." : responseBody;
            _log.LogWarning("Grok HTTP {Status} (attempt {N}/{Max}, model={Model}): {Body}",
                response.StatusCode, attempt, _retryDelays.Length, model, bodyPreview);
            lastEx = new HttpRequestException($"Grok HTTP {status}: {bodyPreview}");
            response.Dispose();

            if (!transient) break;
        }

        throw new InvalidOperationException(
            $"Grok call failed after {_retryDelays.Length} attempts (model={model}). Last error: {lastEx?.Message}",
            lastEx);
    }

    // Folds a /chat/completions SSE stream back into the JSON a non-streamed call returns, so
    // PostChatAsync parses one shape. Every line re-arms the watchdog, reasoning chunks included.
    private async Task<string> ReadChatStreamAsync(HttpResponseMessage response, CancellationTokenSource watchdog)
    {
        await using var body = await response.Content.ReadAsStreamAsync(watchdog.Token);
        using var reader = new StreamReader(body, Encoding.UTF8);
        var content = new StringBuilder();
        JsonElement? usage = null;

        while (await reader.ReadLineAsync(watchdog.Token) is { } line)
        {
            watchdog.CancelAfter(_streamIdleTimeout);
            // Blank lines separate events; lines starting with ':' are SSE keep-alive comments.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                var assembled = new Dictionary<string, object>
                {
                    ["choices"] = new[] { new { message = new { content = content.ToString() } } },
                };
                if (usage is { } u) assembled["usage"] = u;
                return JsonSerializer.Serialize(assembled);
            }

            using var chunk = JsonDocument.Parse(data);
            var root = chunk.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new HttpRequestException($"Grok stream error: {error}");
            // With include_usage the totals come in a final chunk; earlier chunks carry null.
            if (root.TryGetProperty("usage", out var chunkUsage) && chunkUsage.ValueKind == JsonValueKind.Object)
                usage = chunkUsage.Clone();
            // Reasoning arrives as delta.reasoning_content and is not part of the answer.
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                content.Append(text.GetString());
        }

        throw new IOException("xAI closed the stream before [DONE].");
    }

    private TimeoutException OverallTimeout(string model, int attempt, Exception inner)
    {
        _log.LogWarning("Grok timeout after {Seconds}s (attempt {N}/{Max}, model={Model}), not retried",
            _http.Timeout.TotalSeconds, attempt, _retryDelays.Length, model);
        return new TimeoutException(
            $"xAI sent no answer within {_http.Timeout.TotalSeconds:0}s (model={model}). Not retried, because a " +
            "retry would restart the same reasoning from scratch. Raise GROK_MCP_HTTP_TIMEOUT_SEC if calls this long are expected.",
            inner);
    }

    private static string TryExtractFirstUserText(IEnumerable<object> messages)
    {
        try
        {
            foreach (var m in messages)
            {
                var role = m.GetType().GetProperty("role")?.GetValue(m) as string;
                if (role != "user") continue;
                var content = m.GetType().GetProperty("content")?.GetValue(m);
                if (content is string s) return s;
                // Multimodal: list of content parts
                if (content is System.Collections.IEnumerable parts)
                {
                    foreach (var part in parts)
                    {
                        var type = part.GetType().GetProperty("type")?.GetValue(part) as string;
                        if (type == "text")
                            return part.GetType().GetProperty("text")?.GetValue(part) as string ?? "";
                    }
                }
            }
        }
        catch { /* preview is best-effort */ }
        return "";
    }
}

using System.Net;
using System.Net.Http;
using GrokMcp.Config;
using GrokMcp.Services;
using GrokMcp.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrokMcp.Tests;

public class GrokClientTests
{
    private const string ApiBase = "https://api.example.test/v1";

    private static (GrokClient Client, FakeHttpMessageHandler Handler) Build(string videoModel = "")
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        var opts = Options.Create(new GrokOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = ApiBase,
            ChatModel = "test-chat",
            CreativeModel = "test-vision",
            FastModel = "test-fast",
            MultiAgentModel = "test-multi-agent",
            ImageModel = "test-image",
            VideoModel = videoModel,
        });
        var client = new GrokClient(http, opts, NullLogger<GrokClient>.Instance);
        // Zero out retry delays so transient-error tests don't wait on the wall clock.
        client._retryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
        return (client, handler);
    }

    private const string SuccessChatJson = """
        {
          "choices": [{"message": {"content": "hello back"}}],
          "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}
        }
        """;

    [Fact]
    public async Task ChatAsync_success_returns_content_and_token_usage()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            model: null,
            temperature: 0.7f,
            reasoningEffort: null,
            conversationId: null,
            CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(5, result.CompletionTokens);
        Assert.Equal(15, result.TotalTokens);
        Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/chat/completions", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task ChatAsync_4xx_breaks_out_of_retry_loop_immediately()
    {
        var (client, handler) = Build();
        handler.EnqueueStatus(HttpStatusCode.BadRequest, "bad payload");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChatAsync_5xx_then_success_retries_and_returns()
    {
        var (client, handler) = Build();
        handler.EnqueueStatus(HttpStatusCode.InternalServerError, "boom");
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_429_is_treated_as_transient_and_retries()
    {
        var (client, handler) = Build();
        handler.EnqueueStatus(HttpStatusCode.TooManyRequests, "slow down");
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_all_attempts_fail_throws_with_last_error_message()
    {
        var (client, handler) = Build();
        for (var i = 0; i < 3; i++)
            handler.EnqueueStatus(HttpStatusCode.InternalServerError, $"boom-{i}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None));

        Assert.Contains("failed after 3 attempts", ex.Message);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_network_failure_is_treated_as_transient_and_retries()
    {
        var (client, handler) = Build();
        handler.EnqueueException(new HttpRequestException("connection refused"));
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, null, CancellationToken.None);

        Assert.Equal("hello back", result.Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ChatAsync_with_reasoning_effort_serializes_into_body()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, reasoningEffort: "high", conversationId: null, CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("\"reasoning_effort\":\"high\"", req.Body);
    }

    [Fact]
    public async Task ChatAsync_without_reasoning_effort_omits_field()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, reasoningEffort: null, conversationId: null, CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.DoesNotContain("reasoning_effort", req.Body);
    }

    // /responses wraps the answer differently from /chat/completions: the text sits in
    // output[].content[].text, and the array also carries non-message items (reasoning) that
    // must be skipped rather than concatenated into the answer.
    [Fact]
    public async Task ResponsesAsync_extracts_output_text_and_skips_non_message_items()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "output": [
                {"type": "reasoning", "id": "rs_1"},
                {"type": "message", "role": "assistant",
                 "content": [{"type": "output_text", "text": "team answer"}]}
              ],
              "usage": {"input_tokens": 10, "output_tokens": 5, "total_tokens": 15}
            }
            """);

        var result = await client.ResponsesAsync(
            new object[] { new { role = "user", content = "hi" } },
            model: null, temperature: 0.7f, reasoningEffort: "xhigh",
            conversationId: null, CancellationToken.None);

        Assert.Equal("team answer", result.Content);
        // /responses reports input_tokens/output_tokens, not prompt_tokens/completion_tokens.
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(5, result.CompletionTokens);
        Assert.Equal(15, result.TotalTokens);

        var req = Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/responses", req.Uri.ToString());
        Assert.Contains("\"model\":\"test-multi-agent\"", req.Body);
        Assert.Contains("\"input\":", req.Body);
        // xAI documents the nested form for /responses; the flat field is only a fallback there.
        Assert.Contains("\"reasoning\":{\"effort\":\"xhigh\"}", req.Body);
        Assert.DoesNotContain("reasoning_effort", req.Body);
    }

    [Fact]
    public async Task ChatAsync_with_conversation_id_sets_x_grok_conv_id_header()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, conversationId: "sess-abc-123", CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.True(req.Headers.TryGetValues("x-grok-conv-id", out var values));
        Assert.Equal("sess-abc-123", values!.Single());
    }

    [Fact]
    public async Task ChatAsync_without_conversation_id_does_not_set_header()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        await client.ChatAsync(
            new object[] { new { role = "user", content = "hi" } },
            null, 0.7f, null, conversationId: null, CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.False(req.Headers.Contains("x-grok-conv-id"));
    }

    [Fact]
    public async Task ImagesAsync_no_inputs_posts_to_generations_endpoint()
    {
        var (client, handler) = Build();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var b64 = Convert.ToBase64String(pngBytes);
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        var bytesList = await client.ImagesAsync(
            prompt: "a circle",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            resolution: null,
            quality: null,
            responseFormat: "b64_json",
            inputs: null,
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/images/generations", handler.Requests[0].Uri.ToString());
        Assert.Single(bytesList);
        Assert.Equal(pngBytes, bytesList[0]);
    }

    [Fact]
    public async Task ImagesAsync_with_resolution_serializes_into_body()
    {
        var (client, handler) = Build();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var b64 = Convert.ToBase64String(pngBytes);
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        await client.ImagesAsync(
            prompt: "a circle",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            resolution: "2k",
            quality: null,
            responseFormat: "b64_json",
            inputs: null,
            CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("\"resolution\":\"2k\"", req.Body);
    }

    [Fact]
    public async Task ImagesAsync_without_resolution_omits_field()
    {
        var (client, handler) = Build();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var b64 = Convert.ToBase64String(pngBytes);
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        await client.ImagesAsync(
            prompt: "a circle",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            resolution: null,
            quality: null,
            responseFormat: "b64_json",
            inputs: null,
            CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.DoesNotContain("resolution", req.Body);
    }

    [Fact]
    public async Task ImagesAsync_with_quality_serializes_into_body()
    {
        var (client, handler) = Build();
        var b64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        await client.ImagesAsync(
            prompt: "a circle",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            resolution: null,
            quality: "medium",
            responseFormat: "b64_json",
            inputs: null,
            CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Contains("\"quality\":\"medium\"", req.Body);
    }

    [Fact]
    public async Task ImagesAsync_without_quality_omits_field()
    {
        var (client, handler) = Build();
        var b64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        await client.ImagesAsync(
            prompt: "a circle",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            resolution: null,
            quality: null,
            responseFormat: "b64_json",
            inputs: null,
            CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.DoesNotContain("quality", req.Body);
    }

    [Fact]
    public async Task ImagesAsync_with_inputs_posts_to_edits_endpoint()
    {
        var (client, handler) = Build();
        var b64 = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF });
        handler.EnqueueJson(HttpStatusCode.OK, $$"""{"data":[{"b64_json":"{{b64}}"}]}""");

        var inputs = new object[] { new { url = "data:image/png;base64,AAAA" } };
        var bytesList = await client.ImagesAsync(
            prompt: "make it gold",
            model: null,
            n: 1,
            aspectRatio: "1:1",
            resolution: null,
            quality: null,
            responseFormat: "b64_json",
            inputs: inputs,
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/images/edits", handler.Requests[0].Uri.ToString());
        Assert.Single(bytesList);
    }

    [Fact]
    public async Task ImagesAsync_decodes_multiple_b64_json_entries()
    {
        var (client, handler) = Build();
        var b1 = Convert.ToBase64String(new byte[] { 0x01, 0x02 });
        var b2 = Convert.ToBase64String(new byte[] { 0x03, 0x04 });
        handler.EnqueueJson(HttpStatusCode.OK,
            $$"""{"data":[{"b64_json":"{{b1}}"},{"b64_json":"{{b2}}"}]}""");

        var bytesList = await client.ImagesAsync(
            "p", null, n: 2, "1:1", null, null, "b64_json", inputs: null, CancellationToken.None);

        Assert.Equal(2, bytesList.Count);
        Assert.Equal(new byte[] { 0x01, 0x02 }, bytesList[0]);
        Assert.Equal(new byte[] { 0x03, 0x04 }, bytesList[1]);
    }

    [Fact]
    public async Task VisionAsync_posts_to_chat_completions_with_image_url_content()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var images = new object[] { new { url = "https://example.com/x.jpg" } };
        var text = await client.VisionAsync(
            prompt: "what is this",
            imageObjects: images,
            model: null,
            detail: "auto",
            temperature: 0.2f,
            CancellationToken.None);

        Assert.Equal("hello back", text);
        var req = Assert.Single(handler.Requests);
        Assert.Equal($"{ApiBase}/chat/completions", req.Uri.ToString());
        Assert.Contains("image_url", req.Body);
        Assert.Contains("https://example.com/x.jpg", req.Body);
    }

    // ----- VideosAsync -----

    private const string SuccessVideoStartJson = """{"request_id":"req-123"}""";

    [Fact]
    public async Task VideosAsync_success_polls_until_done_and_downloads_video()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        var videoBytes = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 };
        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK, """{"status":"pending"}""");
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4","duration":6}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, videoBytes);

        var result = await client.VideosAsync(
            prompt: "a cat running",
            model: null,
            duration: 8,
            aspectRatio: "16:9",
            resolution: "720p",
            imageInput: null,
            generateAudio: null,
            CancellationToken.None);

        Assert.Equal(videoBytes, result.Bytes);
        Assert.Equal(6, result.DurationSeconds);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal($"{ApiBase}/videos/generations", handler.Requests[0].Uri.ToString());
        Assert.Equal($"{ApiBase}/videos/req-123", handler.Requests[1].Uri.ToString());
        Assert.Equal($"{ApiBase}/videos/req-123", handler.Requests[2].Uri.ToString());
        Assert.Equal("https://cdn.example.test/v.mp4", handler.Requests[3].Uri.ToString());
        Assert.Equal("Bearer", handler.Requests[1].Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.Requests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task VideosAsync_serializes_optional_fields_into_body()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4","duration":3}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 1, 2, 3 });

        var imageInput = new { url = "data:image/png;base64,AAAA" };
        await client.VideosAsync(
            prompt: "zoom in",
            model: "custom-video-model",
            duration: 5,
            aspectRatio: "9:16",
            resolution: "1080p",
            imageInput: imageInput,
            generateAudio: true,
            CancellationToken.None);

        var postReq = handler.Requests[0];
        Assert.Equal($"{ApiBase}/videos/generations", postReq.Uri.ToString());
        Assert.Contains("\"model\":\"custom-video-model\"", postReq.Body);
        Assert.Contains("\"duration\":5", postReq.Body);
        Assert.Contains("\"aspect_ratio\":\"9:16\"", postReq.Body);
        Assert.Contains("\"resolution\":\"1080p\"", postReq.Body);
        Assert.Contains("\"image\":{\"url\":\"data:image/png;base64,AAAA\"}", postReq.Body);
        Assert.Contains("\"generate_audio\":true", postReq.Body);
    }

    // Since 2026-07-31 grok-imagine-video-1.5 serves text-to-video too, so one default model
    // covers both modes. An empty VideoModel option still resolves to it rather than to nothing.
    [Fact]
    public async Task VideosAsync_text_to_video_falls_back_to_grok_imagine_video_1_5_and_omits_optional_fields()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4"}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 1 });

        var result = await client.VideosAsync("p", null, 8, null, null, null, null, CancellationToken.None);

        Assert.Null(result.DurationSeconds);
        var postReq = handler.Requests[0];
        Assert.Contains("\"model\":\"grok-imagine-video-1.5\"", postReq.Body);
        Assert.DoesNotContain("aspect_ratio", postReq.Body);
        Assert.DoesNotContain("resolution", postReq.Body);
        Assert.DoesNotContain("\"image\"", postReq.Body);
        Assert.DoesNotContain("generate_audio", postReq.Body);
    }

    [Fact]
    public async Task VideosAsync_image_to_video_uses_the_same_default_model()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4"}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 1 });

        var imageInput = new { url = "data:image/png;base64,AAAA" };
        await client.VideosAsync("p", null, 8, null, null, imageInput, null, CancellationToken.None);

        Assert.Contains("\"model\":\"grok-imagine-video-1.5\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task VideosAsync_serializes_generate_audio_false_when_provided()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4"}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 1 });

        await client.VideosAsync("p", null, 8, null, null, null, false, CancellationToken.None);

        Assert.Contains("\"generate_audio\":false", handler.Requests[0].Body);
    }

    [Fact]
    public async Task VideosAsync_pinned_video_model_wins_over_default()
    {
        var (client, handler) = Build(videoModel: "pinned-video-model");
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4"}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 1 });

        await client.VideosAsync("p", null, 8, null, null, null, null, CancellationToken.None);

        Assert.Contains("\"model\":\"pinned-video-model\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task VideosAsync_explicit_model_param_wins_over_pinned_video_model()
    {
        var (client, handler) = Build(videoModel: "pinned-video-model");
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4"}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 1 });

        await client.VideosAsync("p", "explicit-model", 8, null, null, null, null, CancellationToken.None);

        Assert.Contains("\"model\":\"explicit-model\"", handler.Requests[0].Body);
        Assert.DoesNotContain("pinned-video-model", handler.Requests[0].Body);
    }

    [Fact]
    public async Task VideosAsync_failed_status_throws_with_request_id_and_detail()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK, """{"status":"failed","error":"content policy violation"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.VideosAsync("p", null, 8, null, null, null, null, CancellationToken.None));

        Assert.Contains("failed", ex.Message);
        Assert.Contains("req-123", ex.Message);
        Assert.Contains("content policy violation", ex.Message);
    }

    [Fact]
    public async Task VideosAsync_expired_status_throws()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueJson(HttpStatusCode.OK, """{"status":"expired"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.VideosAsync("p", null, 8, null, null, null, null, CancellationToken.None));

        Assert.Contains("expired", ex.Message);
        Assert.Contains("req-123", ex.Message);
    }

    [Fact]
    public async Task VideosAsync_missing_request_id_throws()
    {
        var (client, handler) = Build();
        handler.EnqueueJson(HttpStatusCode.OK, "{}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.VideosAsync("p", null, 8, null, null, null, null, CancellationToken.None));

        Assert.Contains("request_id", ex.Message);
    }

    [Fact]
    public async Task VideosAsync_poll_network_failure_is_not_fatal_and_keeps_polling()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);
        handler.EnqueueException(new HttpRequestException("connection reset"));
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4","duration":2}}""");
        handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 9 });

        var result = await client.VideosAsync("p", null, 8, null, null, null, null, CancellationToken.None);

        Assert.Equal(new byte[] { 9 }, result.Bytes);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task VideosAsync_timeout_throws_with_request_id_before_any_poll()
    {
        var (client, handler) = Build();
        client._videoPollInterval = TimeSpan.Zero;
        client._videoPollTimeout = TimeSpan.Zero;

        handler.EnqueueJson(HttpStatusCode.OK, SuccessVideoStartJson);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.VideosAsync("p", null, 8, null, null, null, null, CancellationToken.None));

        Assert.Contains("timed out", ex.Message);
        Assert.Contains("req-123", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Constructor_with_empty_api_key_throws()
    {
        var opts = Options.Create(new GrokOptions { ApiKey = "" });
        var http = new HttpClient(new FakeHttpMessageHandler());
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new GrokClient(http, opts, NullLogger<GrokClient>.Instance));
        Assert.Contains("XAI_API_KEY", ex.Message);
    }
}

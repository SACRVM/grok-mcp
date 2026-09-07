using System.Net;
using System.Net.Http;
using GrokMcp.Config;
using GrokMcp.Services;
using GrokMcp.Tests.Fakes;
using GrokMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace GrokMcp.Tests;

// These tests compose the real services (GrokTools → GrokClient → ImageInputResolver → ImageWriter)
// but replace the HttpMessageHandler so no real HTTP traffic happens. Validation paths and
// resolver-error paths assert that no HTTP call is made.
public class GrokToolsTests : IDisposable
{
    private const string ApiBase = "https://api.example.test/v1";
    private readonly string _tmp;

    private readonly FakeHttpMessageHandler _handler = new();
    private readonly GrokTools _tools;
    private readonly ChatSessionStore _sessions;

    private const string SuccessChatJson = """
        {
          "choices": [{"message": {"content": "answer"}}],
          "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}
        }
        """;

    // /responses shape — multi-agent only. Note the different envelope and usage field names.
    private const string SuccessResponsesJson = """
        {
          "output": [
            {"type": "reasoning", "id": "rs_1"},
            {"type": "message", "role": "assistant",
             "content": [{"type": "output_text", "text": "team answer"}]}
          ],
          "usage": {"input_tokens": 10, "output_tokens": 5, "total_tokens": 15}
        }
        """;

    public GrokToolsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "grok-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);

        var http = new HttpClient(_handler);
        var opts = Options.Create(new GrokOptions
        {
            ApiKey = "test-key",
            ApiBaseUrl = ApiBase,
            ChatModel = "test-chat",
            CreativeModel = "test-vision",
            FastModel = "test-fast",
            MultiAgentModel = "test-multi-agent",
            ImageModel = "test-image",
            SessionTurnCap = 10,
        });
        var grok = new GrokClient(http, opts, NullLogger<GrokClient>.Instance);
        grok._retryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
        grok._videoPollInterval = TimeSpan.Zero;

        _sessions = new ChatSessionStore(opts);
        var resolver = new ImageInputResolver();
        var writer = new ImageWriter();
        _tools = new GrokTools(grok, _sessions, resolver, writer, opts, NullLogger<GrokTools>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    // ----- Validation paths (must short-circuit before any HTTP call) -----

    [Fact]
    public async Task GrokChat_empty_message_returns_error_and_makes_no_http_call()
    {
        var result = await _tools.GrokChat("");
        AssertError(result, "message must not be empty");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateImage_empty_prompt_returns_error()
    {
        var result = await _tools.GrokGenerateImage("", Path.Combine(_tmp, "x.jpg"));
        AssertError(result, "prompt must not be empty");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateImage_n_out_of_range_returns_error()
    {
        var result = await _tools.GrokGenerateImage("p", Path.Combine(_tmp, "x.jpg"), n: 99);
        AssertError(result, "n must be between 1 and 10");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokEditImage_empty_images_returns_error()
    {
        var result = await _tools.GrokEditImage("p", Array.Empty<string>(), Path.Combine(_tmp, "x.jpg"));
        AssertError(result, "images must contain at least one entry");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokEditImage_n_out_of_range_returns_error()
    {
        var result = await _tools.GrokEditImage(
            "p",
            new[] { "https://example.com/a.jpg" },
            Path.Combine(_tmp, "x.jpg"),
            n: 9);
        AssertError(result, "n must be between 1 and 4");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokEditImage_more_than_five_images_returns_error()
    {
        var images = new[]
        {
            "https://example.com/1.jpg", "https://example.com/2.jpg", "https://example.com/3.jpg",
            "https://example.com/4.jpg", "https://example.com/5.jpg", "https://example.com/6.jpg",
        };
        var result = await _tools.GrokEditImage("p", images, Path.Combine(_tmp, "x.jpg"));
        AssertError(result, "at most 5");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokDescribeImage_empty_prompt_returns_error()
    {
        var result = await _tools.GrokDescribeImage("", new[] { "https://example.com/a.jpg" });
        AssertError(result, "prompt must not be empty");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokDescribeImage_missing_local_file_returns_error_with_path_in_message()
    {
        var missing = Path.Combine(_tmp, "absent.jpg");
        var result = await _tools.GrokDescribeImage("describe", new[] { missing });
        AssertError(result, missing);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokChat_invalid_reasoning_effort_returns_error()
    {
        var result = await _tools.GrokChat("hi", reasoning_effort: "foo");
        AssertError(result, "reasoning_effort must be one of");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateImage_invalid_resolution_returns_error()
    {
        var result = await _tools.GrokGenerateImage("p", Path.Combine(_tmp, "x.png"), resolution: "4k");
        AssertError(result, "resolution must be '1k' or '2k'");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokEditImage_invalid_resolution_returns_error()
    {
        var result = await _tools.GrokEditImage(
            "p",
            new[] { "https://example.com/a.jpg" },
            Path.Combine(_tmp, "x.png"),
            resolution: "4k");
        AssertError(result, "resolution must be '1k' or '2k'");
        Assert.Empty(_handler.Requests);
    }

    // 'high' is in xAI's schema but grok-imagine-image-2.0 rejects it with HTTP 400, so it is
    // refused here alongside plain nonsense — before any credit is spent.
    [Theory]
    [InlineData("high")]
    [InlineData("ultra")]
    public async Task GrokGenerateImage_invalid_quality_returns_error(string quality)
    {
        var result = await _tools.GrokGenerateImage("p", Path.Combine(_tmp, "x.png"), quality: quality);
        AssertError(result, "quality must be one of");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokEditImage_invalid_quality_returns_error()
    {
        var result = await _tools.GrokEditImage(
            "p",
            new[] { "https://example.com/a.jpg" },
            Path.Combine(_tmp, "x.png"),
            quality: "high");
        AssertError(result, "quality must be one of");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateVideo_empty_prompt_returns_error()
    {
        var result = await _tools.GrokGenerateVideo("", Path.Combine(_tmp, "x.mp4"));
        AssertError(result, "prompt must not be empty");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateVideo_duration_out_of_range_returns_error()
    {
        var result = await _tools.GrokGenerateVideo("p", Path.Combine(_tmp, "x.mp4"), duration: 30);
        AssertError(result, "duration must be between 1 and 15");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateVideo_invalid_resolution_returns_error()
    {
        var result = await _tools.GrokGenerateVideo("p", Path.Combine(_tmp, "x.mp4"), resolution: "4k");
        AssertError(result, "resolution must be one of: 480p, 720p, 1080p");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateVideo_relative_output_path_returns_error_and_makes_no_http_call()
    {
        var result = await _tools.GrokGenerateVideo("p", "relative.mp4");
        AssertError(result, "absolute path");
        Assert.Empty(_handler.Requests);
    }

    // ----- Success paths -----

    [Fact]
    public async Task GrokChat_success_returns_assistant_text()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await _tools.GrokChat("hi");

        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Equal("answer", text.Text);
    }

    [Fact]
    public async Task GrokChat_with_session_id_appends_user_and_assistant_turns()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var session = $"test-session-{Guid.NewGuid():N}";
        await _tools.GrokChat("first message", session_id: session);

        var snap = _sessions.Snapshot(session);
        Assert.Equal(2, snap.Count);
        Assert.Equal("user", snap[0].Role);
        Assert.Equal("first message", snap[0].Content);
        Assert.Equal("assistant", snap[1].Role);
        Assert.Equal("answer", snap[1].Content);
    }

    [Fact]
    public async Task GrokChat_reset_session_clears_prior_turns_before_running()
    {
        var session = $"test-session-{Guid.NewGuid():N}";
        _sessions.Append(session, new ChatTurn("user", "old"));
        _sessions.Append(session, new ChatTurn("assistant", "older"));

        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);
        await _tools.GrokChat("fresh", session_id: session, reset_session: true);

        var snap = _sessions.Snapshot(session);
        Assert.Equal(2, snap.Count);
        Assert.Equal("fresh", snap[0].Content);
        Assert.Equal("answer", snap[1].Content);
    }

    [Fact]
    public async Task GrokChat_grok_failure_returns_error_result_with_message()
    {
        // 4xx breaks out of retry → bubbles up as InvalidOperationException → caught and wrapped.
        _handler.EnqueueStatus(HttpStatusCode.BadRequest, "bad request body");

        var result = await _tools.GrokChat("hi");

        AssertError(result, "Chat failed");
    }

    [Fact]
    public async Task GrokChat_reasoning_effort_high_is_accepted_and_serialized()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await _tools.GrokChat("hi", reasoning_effort: "high");

        Assert.False(result.IsError ?? false);
        var req = Assert.Single(_handler.Requests);
        Assert.Contains("\"reasoning_effort\":\"high\"", req.Body);
    }

    // 'none' used to be the documented way to get a cheap non-reasoning answer, but the flagship
    // model rejects it with HTTP 400. grok_chat_fast is the replacement; fail before spending a call.
    [Fact]
    public async Task GrokChat_rejects_none_which_the_flagship_model_cannot_serve()
    {
        var result = await _tools.GrokChat("hi", reasoning_effort: "none");

        AssertError(result, "reasoning_effort must be one of");
        Assert.Empty(_handler.Requests);
    }

    // grok-4.6 accepts 'xhigh' as plain deeper reasoning; 1.2.0 refused it because back then the
    // value only existed on the multi-agent model.
    [Fact]
    public async Task GrokChat_reasoning_effort_xhigh_is_accepted_and_serialized()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await _tools.GrokChat("hi", reasoning_effort: "xhigh");

        Assert.False(result.IsError ?? false);
        var req = Assert.Single(_handler.Requests);
        Assert.Contains("\"reasoning_effort\":\"xhigh\"", req.Body);
    }

    [Fact]
    public async Task GrokChatFast_uses_fast_model_and_never_sends_reasoning_effort()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var result = await _tools.GrokChatFast("hi");

        Assert.False(result.IsError ?? false);
        var req = Assert.Single(_handler.Requests);
        Assert.Contains("\"model\":\"test-fast\"", req.Body);
        Assert.DoesNotContain("reasoning_effort", req.Body);
        Assert.EndsWith("/chat/completions", req.Uri.AbsolutePath);
    }

    [Fact]
    public async Task GrokChatFast_with_session_id_shares_history_with_grok_chat()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessChatJson);

        var session = $"test-session-{Guid.NewGuid():N}";
        await _tools.GrokChatFast("first message", session_id: session);

        var snap = _sessions.Snapshot(session);
        Assert.Equal(2, snap.Count);
        Assert.Equal("first message", snap[0].Content);
        Assert.Equal("answer", snap[1].Content);
    }

    // xAI's table for this model: low/medium = 4 agents, high/xhigh = 16. 1.2.0 sent 'high' for 4
    // and silently paid for 16 — the API accepts any effort value — so the mapping is pinned here.
    [Theory]
    [InlineData(4, "medium")]
    [InlineData(16, "xhigh")]
    public async Task GrokChatMultiAgent_maps_agent_count_onto_reasoning_effort(int agents, string expectedEffort)
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessResponsesJson);

        var result = await _tools.GrokChatMultiAgent("hi", agents: agents);

        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Equal("team answer", text.Text);

        var req = Assert.Single(_handler.Requests);
        // /responses wants the nested object; the flat field is only a fallback there.
        Assert.Contains($"\"reasoning\":{{\"effort\":\"{expectedEffort}\"}}", req.Body);
        Assert.DoesNotContain("reasoning_effort", req.Body);
        Assert.Contains("\"model\":\"test-multi-agent\"", req.Body);
    }

    // The multi-agent model is rejected on /chat/completions, so hitting /responses is the
    // whole point of this tool — assert the endpoint, not just the payload.
    [Fact]
    public async Task GrokChatMultiAgent_posts_to_responses_endpoint_with_input_not_messages()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, SuccessResponsesJson);

        await _tools.GrokChatMultiAgent("hi");

        var req = Assert.Single(_handler.Requests);
        Assert.EndsWith("/responses", req.Uri.AbsolutePath);
        Assert.Contains("\"input\":", req.Body);
        Assert.DoesNotContain("\"messages\":", req.Body);
    }

    [Fact]
    public async Task GrokChatMultiAgent_invalid_agent_count_returns_error_and_makes_no_http_call()
    {
        var result = await _tools.GrokChatMultiAgent("hi", agents: 8);

        AssertError(result, "agents must be either 4 or 16");
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GrokGenerateVideo_success_saves_mp4_and_returns_text_summary_with_no_image_block()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"request_id":"req-xyz"}""");
        _handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4","duration":4}}""");
        var videoBytes = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 };
        _handler.EnqueueBytes(HttpStatusCode.OK, videoBytes);

        var outPath = Path.Combine(_tmp, "clip.mp4");
        var result = await _tools.GrokGenerateVideo("a cat running", outPath);

        Assert.False(result.IsError ?? false);
        Assert.Single(result.Content);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Contains(outPath, text.Text);
        Assert.Contains("4s", text.Text);
        Assert.True(File.Exists(outPath));
        // The tool states the audio choice explicitly (default true) instead of relying on xAI's default.
        Assert.Contains("\"generate_audio\":true", _handler.Requests[0].Body);
    }

    [Fact]
    public async Task GrokGenerateVideo_generate_audio_false_is_serialized()
    {
        _handler.EnqueueJson(HttpStatusCode.OK, """{"request_id":"req-xyz"}""");
        _handler.EnqueueJson(HttpStatusCode.OK,
            """{"status":"done","video":{"url":"https://cdn.example.test/v.mp4","duration":1}}""");
        _handler.EnqueueBytes(HttpStatusCode.OK, new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 });

        await _tools.GrokGenerateVideo("p", Path.Combine(_tmp, "silent.mp4"), duration: 1, generate_audio: false);

        Assert.Contains("\"generate_audio\":false", _handler.Requests[0].Body);
    }

    [Fact]
    public async Task GrokGenerateVideo_grok_failure_returns_error_result_with_message()
    {
        _handler.EnqueueStatus(HttpStatusCode.BadRequest, "bad video request");

        var result = await _tools.GrokGenerateVideo("p", Path.Combine(_tmp, "x.mp4"));

        AssertError(result, "Video generation failed");
    }

    // ----- Helpers -----

    private static void AssertError(CallToolResult result, string expectedFragment)
    {
        Assert.True(result.IsError, "Expected IsError = true on result");
        var text = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Contains(expectedFragment, text.Text);
    }
}

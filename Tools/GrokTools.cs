using System.ComponentModel;
using GrokMcp.Config;
using GrokMcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GrokMcp.Tools;

[McpServerToolType]
public class GrokTools
{
    private readonly GrokClient _grok;
    private readonly ChatSessionStore _sessions;
    private readonly ImageInputResolver _imgResolver;
    private readonly ImageWriter _imgWriter;
    private readonly GrokOptions _opts;
    private readonly ILogger<GrokTools> _log;

    public GrokTools(
        GrokClient grok,
        ChatSessionStore sessions,
        ImageInputResolver imgResolver,
        ImageWriter imgWriter,
        IOptions<GrokOptions> opts,
        ILogger<GrokTools> log)
    {
        _grok = grok;
        _sessions = sessions;
        _imgResolver = imgResolver;
        _imgWriter = imgWriter;
        _opts = opts.Value;
        _log = log;
    }

    [McpServerTool(Name = "grok_chat"), Description(
        "Send a chat completion to xAI Grok's flagship model (grok-4.6: 500k context, vision, function calling). " +
        "This model always reasons before answering and cannot be told not to — for a fast non-reasoning answer " +
        "call grok_chat_fast instead, and for a hard problem worth a team of agents call grok_chat_multi_agent. " +
        "Stateless by default. Pass session_id to enable an in-memory thread that persists for the lifetime of " +
        "this server process; the same session_id on subsequent calls appends to and replays prior turns. " +
        "Returns the assistant text only.")]
    public async Task<CallToolResult> GrokChat(
        [Description("User message (required).")] string message,
        [Description("Optional system prompt prepended to the conversation. On a session, replaces any prior system prompt.")] string? system = null,
        [Description("Model override. Defaults to grok-4.6. Pass grok-4.3 when you need more than 500k context (it has 1M, at the cost of a weaker model), or grok-4.5 for the previous flagship.")] string? model = null,
        [Description("Sampling temperature 0.0-2.0. Default 0.7.")] float temperature = 0.7f,
        [Description("Optional session id. Same id reuses in-memory chat history within this server process. Omit for stateless one-shot. When set, also routes to the same xAI server for prompt-prefix caching (cached input is billed at a quarter of normal on grok-4.6).")] string? session_id = null,
        [Description("If true, clears the named session before this turn. No effect when session_id is null.")] bool reset_session = false,
        [Description("Reasoning depth: 'low', 'medium', 'high' or 'xhigh'. Omit to let xAI default apply (grok-4.6 defaults to 'high'). 'xhigh' is the deepest setting, for problems where answer quality matters more than latency. Reasoning cannot be switched off here — that is what grok_chat_fast is for.")] string? reasoning_effort = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(reasoning_effort) &&
            reasoning_effort is not ("low" or "medium" or "high" or "xhigh"))
            return Error("reasoning_effort must be one of: low, medium, high, xhigh. " +
                         "For a non-reasoning answer use grok_chat_fast; for a team of agents use grok_chat_multi_agent.");

        return await RunChatAsync("grok_chat", message, system, session_id, reset_session,
            (messages, conversationId, token) =>
                _grok.ChatAsync(messages, model, temperature, reasoning_effort, conversationId, token),
            ct);
    }

    [McpServerTool(Name = "grok_chat_fast"), Description(
        "Send a chat completion to a dedicated non-reasoning Grok model (grok-4.20-0309-non-reasoning: 1M context). " +
        "The model answers immediately without thinking first, which makes this the fast, cheap path for lookups, " +
        "classification, extraction, summarising and reformatting. There is deliberately no reasoning option — " +
        "if the answer needs actual thought, call grok_chat. Sessions work exactly as in grok_chat. " +
        "Returns the assistant text only.")]
    public async Task<CallToolResult> GrokChatFast(
        [Description("User message (required).")] string message,
        [Description("Optional system prompt prepended to the conversation. On a session, replaces any prior system prompt.")] string? system = null,
        [Description("Sampling temperature 0.0-2.0. Default 0.7.")] float temperature = 0.7f,
        [Description("Optional session id. Same id reuses in-memory chat history within this server process. Omit for stateless one-shot.")] string? session_id = null,
        [Description("If true, clears the named session before this turn. No effect when session_id is null.")] bool reset_session = false,
        CancellationToken ct = default)
        => await RunChatAsync("grok_chat_fast", message, system, session_id, reset_session,
            (messages, conversationId, token) =>
                _grok.ChatAsync(messages, _opts.FastModel, temperature, reasoningEffort: null, conversationId, token),
            ct);

    [McpServerTool(Name = "grok_chat_multi_agent"), Description(
        "Send a hard problem to Grok's multi-agent model (grok-4.20-multi-agent-0309), where a team of agents works " +
        "the question in parallel and reconciles their answers into one. Markedly slower and far more token-hungry " +
        "than grok_chat — even a trivial question burns thousands of tokens on agent overhead — so reserve it for " +
        "problems where independent cross-checking is actually worth it. The model tends to append a " +
        "'\\confidence{N}' marker to its answer; that is the model's own output, not a wrapper artifact. " +
        "Sessions work exactly as in grok_chat. Returns the assistant text only.")]
    public async Task<CallToolResult> GrokChatMultiAgent(
        [Description("User message (required).")] string message,
        [Description("Optional system prompt prepended to the conversation. On a session, replaces any prior system prompt.")] string? system = null,
        [Description("How many agents work the problem: 4 (default) or 16. 16 explores more widely and costs proportionally more.")] int agents = 4,
        [Description("Sampling temperature 0.0-2.0. Default 0.7.")] float temperature = 0.7f,
        [Description("Optional session id. Same id reuses in-memory chat history within this server process. Omit for stateless one-shot.")] string? session_id = null,
        [Description("If true, clears the named session before this turn. No effect when session_id is null.")] bool reset_session = false,
        CancellationToken ct = default)
    {
        if (agents is not (4 or 16))
            return Error("agents must be either 4 or 16.");

        // xAI expresses agent count through the reasoning effort on this model, and the split is
        // not where one would guess: 'low' and 'medium' both mean 4 agents, 'high' and 'xhigh'
        // both mean 16 (docs.x.ai, Multi Agent → Configuration). 1.2.0 sent 'high' for 4 and
        // silently ran the 16-agent setup — the API never rejects an effort value.
        var effort = agents == 16 ? "xhigh" : "medium";

        return await RunChatAsync("grok_chat_multi_agent", message, system, session_id, reset_session,
            (messages, conversationId, token) =>
                _grok.ResponsesAsync(messages, model: null, temperature, effort, conversationId, token),
            ct);
    }

    // The three chat tools differ only in which model/endpoint they hit; session assembly,
    // history replay and error shaping are identical, so they share this.
    private async Task<CallToolResult> RunChatAsync(
        string toolName,
        string message,
        string? system,
        string? session_id,
        bool reset_session,
        Func<List<object>, string?, CancellationToken, Task<GrokClient.ChatResult>> invoke,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Error("message must not be empty.");

        try
        {
            var messages = new List<object>();

            if (!string.IsNullOrEmpty(session_id))
            {
                if (reset_session) _sessions.Reset(session_id);

                if (!string.IsNullOrWhiteSpace(system))
                    messages.Add(new { role = "system", content = system });

                foreach (var turn in _sessions.Snapshot(session_id))
                    messages.Add(new { role = turn.Role, content = turn.Content });

                messages.Add(new { role = "user", content = message });

                var result = await invoke(messages, session_id, ct);

                _sessions.Append(session_id, new ChatTurn("user", message));
                _sessions.Append(session_id, new ChatTurn("assistant", result.Content));
                return Text(result.Content);
            }

            // Stateless single-shot
            if (!string.IsNullOrWhiteSpace(system))
                messages.Add(new { role = "system", content = system });
            messages.Add(new { role = "user", content = message });

            var oneShot = await invoke(messages, null, ct);
            return Text(oneShot.Content);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Tool} failed", toolName);
            return Error($"Chat failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_generate_image"), Description(
        "Generate one or more images with Grok and save them to disk. " +
        "Saves the bytes to the caller-supplied output_path (must be absolute) and ALSO returns inline image content so the calling agent can see them. " +
        "When n>1, output_path is treated as a template: '-1', '-2', ... is inserted before the extension.")]
    public async Task<CallToolResult> GrokGenerateImage(
        [Description("The image prompt (required).")] string prompt,
        [Description("Absolute filesystem path where the image is saved. Required. For n>1 the index is inserted before the extension. Parent directory will be created if missing.")] string output_path,
        [Description("Number of images. 1-10. Default 1.")] int n = 1,
        [Description("Aspect ratio. One of 1:1, 3:4, 4:3, 9:16, 16:9, 2:3, 3:2, 9:19.5, 19.5:9, 9:20, 20:9, 1:2, 2:1, 21:9, 5:2, or 'auto'. Default '1:1'.")] string aspect_ratio = "1:1",
        [Description("Image resolution: '1k' or '2k'. Omit for xAI default.")] string? resolution = null,
        [Description("Render quality on grok-imagine-image-2.0: 'low', 'medium' or 'auto'. Omit for xAI default ('auto', which currently means 'low' for generation). 'medium' costs more per image. Ignored by the older grok-imagine-image model.")] string? quality = null,
        [Description("Model override. Defaults to grok-imagine-image-2.0. Pass grok-imagine-image for the cheaper first-generation model (no quality control).")] string? model = null,
        [Description("'b64_json' (default) or 'url'. The server fetches URL bytes either way.")] string response_format = "b64_json",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (n < 1 || n > 10)
            return Error("n must be between 1 and 10.");
        if (!string.IsNullOrWhiteSpace(resolution) && resolution is not ("1k" or "2k"))
            return Error("resolution must be '1k' or '2k'.");
        if (!string.IsNullOrWhiteSpace(quality) && quality is not ("low" or "medium" or "auto"))
            return Error("quality must be one of: low, medium, auto.");

        try
        {
            var bytesList = await _grok.ImagesAsync(prompt, model, n, aspect_ratio, resolution, quality, response_format, inputs: null, ct);
            return SaveAndPackage(output_path, bytesList);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_generate_image failed");
            return Error($"Image generation failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_edit_image"), Description(
        "Edit / composite one or more input images with a prompt. " +
        "Inputs are resolved per-item: an http(s) URL is forwarded as-is to xAI; an absolute filesystem path is read and base64-encoded; " +
        "a string starting with 'data:' is forwarded; any other string is treated as raw base64 (wrapped as a data URI). " +
        "Saves the result to output_path and returns the inline image.")]
    public async Task<CallToolResult> GrokEditImage(
        [Description("Edit instructions / prompt (required).")] string prompt,
        [Description("One to five input images (xAI's limit per edit). Each item may be http(s) URL, absolute file path, data: URI, or raw base64. Required. Grok sees them in order as image 1, image 2, ...")] string[] images,
        [Description("Absolute output path for the resulting image (required).")] string output_path,
        [Description("Number of result images. 1-4. Default 1.")] int n = 1,
        [Description("Aspect ratio. One of 1:1, 3:4, 4:3, 9:16, 16:9, 2:3, 3:2, 9:19.5, 19.5:9, 9:20, 20:9, 1:2, 2:1, 21:9, 5:2, or 'auto'. Default '1:1'. Pass 'auto' to follow the first input image.")] string aspect_ratio = "1:1",
        [Description("Image resolution: '1k' or '2k'. Omit for xAI default.")] string? resolution = null,
        [Description("Render quality on grok-imagine-image-2.0: 'low', 'medium' or 'auto'. Omit for xAI default ('auto', which currently means 'medium' for edits). Ignored by the older grok-imagine-image model.")] string? quality = null,
        [Description("Model override. Defaults to grok-imagine-image-2.0. Pass grok-imagine-image for the cheaper first-generation model (no quality control).")] string? model = null,
        [Description("'b64_json' (default) or 'url'.")] string response_format = "b64_json",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (images == null || images.Length == 0)
            return Error("images must contain at least one entry.");
        if (images.Length > 5)
            return Error("images must contain at most 5 entries — xAI's limit for a single edit.");
        if (n < 1 || n > 4)
            return Error("n must be between 1 and 4 for edits.");
        if (!string.IsNullOrWhiteSpace(resolution) && resolution is not ("1k" or "2k"))
            return Error("resolution must be '1k' or '2k'.");
        if (!string.IsNullOrWhiteSpace(quality) && quality is not ("low" or "medium" or "auto"))
            return Error("quality must be one of: low, medium, auto.");

        try
        {
            var resolved = images.Select(_imgResolver.Resolve).ToList();
            var bytesList = await _grok.ImagesAsync(prompt, model, n, aspect_ratio, resolution, quality, response_format, resolved, ct);
            return SaveAndPackage(output_path, bytesList);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_edit_image failed");
            return Error($"Image edit failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_describe_image"), Description(
        "Vision: ask Grok to describe / analyze one or more images. " +
        "Inputs follow the same resolution rules as grok_edit_image (URL / absolute path / data URI / raw base64). " +
        "Returns plain text.")]
    public async Task<CallToolResult> GrokDescribeImage(
        [Description("The question or instruction (e.g. 'Describe this image', 'Read the text', 'What style is this art in?'). Required.")] string prompt,
        [Description("One or more images to analyze. Required.")] string[] images,
        [Description("Vision-capable model. Defaults to grok-4.6.")] string? model = null,
        [Description("Detail level passed through to xAI ('low' | 'high' | 'auto'). Default 'auto'.")] string detail = "auto",
        [Description("Sampling temperature. Default 0.2 for descriptive accuracy.")] float temperature = 0.2f,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (images == null || images.Length == 0)
            return Error("images must contain at least one entry.");

        try
        {
            var resolved = images.Select(_imgResolver.Resolve).ToList();
            var text = await _grok.VisionAsync(prompt, resolved, model, detail, temperature, ct);
            return Text(text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_describe_image failed");
            return Error($"Describe failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "grok_generate_video"), Description(
        "Generate a video with Grok from a text prompt, optionally seeded with a starting image (image-to-video). " +
        "Video generation is asynchronous on xAI's side; this call polls until the video is ready, " +
        "which can block for up to several minutes (poll interval 5s, 10-minute timeout before failing). " +
        "Saves the result as an MP4 to output_path (must be absolute). " +
        "Returns a text summary only — video bytes are not returned inline.")]
    public async Task<CallToolResult> GrokGenerateVideo(
        [Description("The video prompt (required).")] string prompt,
        [Description("Absolute filesystem path where the MP4 is saved. Required. Parent directory will be created if missing.")] string output_path,
        [Description("Video duration in seconds. 1-15. Default 8.")] int duration = 8,
        [Description("Aspect ratio. One of 1:1, 16:9, 9:16, 4:3, 3:4, 3:2, 2:3. Default '16:9'.")] string aspect_ratio = "16:9",
        [Description("Resolution. One of '480p', '720p', '1080p'. Default '720p'. 1080p needs grok-imagine-video-1.5 (the default); the older grok-imagine-video tops out at 720p.")] string resolution = "720p",
        [Description("Model override. Defaults to grok-imagine-video-1.5 for both text-to-video and image-to-video (native 1080p). Set GROK_MCP_VIDEO_MODEL in config.env to change the default.")] string? model = null,
        [Description("Optional starting image for image-to-video. Same resolution rules as grok_edit_image: http(s) URL, absolute file path, data: URI, or raw base64.")] string? image = null,
        [Description("Whether the clip gets a generated audio track. Default true (xAI's default). Pass false for a silent video.")] bool generate_audio = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Error("prompt must not be empty.");
        if (duration < 1 || duration > 15)
            return Error("duration must be between 1 and 15.");
        if (resolution is not ("480p" or "720p" or "1080p"))
            return Error("resolution must be one of: 480p, 720p, 1080p.");
        // Check up front — video generation can take minutes, so fail before spending that
        // time on a call whose output we couldn't save anyway.
        if (!System.IO.Path.IsPathFullyQualified(output_path))
            return Error(
                $"output_path must be an absolute path. Got: {output_path}. " +
                "Reason: the MCP server has its own working directory, separate from the calling agent.");

        try
        {
            object? imageInput = string.IsNullOrWhiteSpace(image) ? null : _imgResolver.Resolve(image);
            var result = await _grok.VideosAsync(prompt, model, duration, aspect_ratio, resolution, imageInput, generate_audio, ct);
            var saved = _imgWriter.WriteVideo(output_path, result.Bytes);

            var summary = result.DurationSeconds is { } d
                ? $"Saved video to: {saved.Path} (duration: {d}s)"
                : $"Saved video to: {saved.Path}";
            return Text(summary);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "grok_generate_video failed");
            return Error($"Video generation failed: {ex.Message}");
        }
    }

    private CallToolResult SaveAndPackage(string outputPath, IReadOnlyList<byte[]> bytesList)
    {
        var saved = _imgWriter.WriteAll(outputPath, bytesList);
        var content = new List<ContentBlock>();

        var pathSummary = saved.Count == 1
            ? $"Saved image to: {saved[0].Path}"
            : "Saved images to:\n" + string.Join("\n", saved.Select(s => $"  - {s.Path}"));
        content.Add(new TextContentBlock { Text = pathSummary });

        foreach (var s in saved)
        {
            content.Add(ImageContentBlock.FromBytes(s.Bytes, "image/png"));
        }

        return new CallToolResult { Content = content, IsError = false };
    }

    private static CallToolResult Text(string text)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
            IsError = false,
        };
    }

    private static CallToolResult Error(string message)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
            IsError = true,
        };
    }
}

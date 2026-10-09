using System;

namespace GrokMcp.Config;

public class GrokOptions
{
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://api.x.ai/v1";
    public string ChatModel { get; set; } = "grok-4.7";
    public string CreativeModel { get; set; } = "grok-4.7";
    // Dedicated non-reasoning model behind grok_chat_fast. Not a "cheap tier" of ChatModel:
    // grok-4.7 (like 4.5 and 4.6) rejects reasoning_effort='none' outright, so the fast path needs its own model.
    public string FastModel { get; set; } = "grok-4.20-0309-non-reasoning";
    // Only reachable via /responses — xAI rejects this model on /chat/completions.
    public string MultiAgentModel { get; set; } = "grok-4.20-multi-agent-0309";
    // The only image model that honours the 'quality' parameter. grok-imagine-image (1.0)
    // accepts it silently and ignores it; grok-imagine-image-quality retires on 2026-11-02.
    public string ImageModel { get; set; } = "grok-imagine-image-2.0";
    // One model for text-to-video and image-to-video since xAI added text-to-video to 1.5
    // (2026-07-31). GROK_MCP_VIDEO_MODEL overrides it; the older grok-imagine-video tops out at 720p.
    public string VideoModel { get; set; } = "grok-imagine-video-1.5";
    // xAI sends nothing until a reasoning run is finished, and a long document at any effort can
    // think past five minutes. 3600 is what xAI's own reasoning examples configure.
    public int HttpTimeoutSeconds { get; set; } = 3600;
    public int SessionTurnCap { get; set; } = 50;
    public int ListenPort { get; set; } = 6677;
    public int LogRetentionDays { get; set; } = 30;
    public string LogDir { get; set; } = "";
    public string LogLevel { get; set; } = "Information";

    public static void BindFromEnvironment(GrokOptions o)
    {
        // Priority: real process env vars > %LOCALAPPDATA% > %USERPROFILE%. The loader only
        // sets keys that aren't already present, so the second file can't overwrite the first
        // and neither file can overwrite a real env var.
        LoadEnvFile(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "grok-mcp", "config.env"));
        LoadEnvFile(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok-mcp", "config.env"));

        o.ApiKey = Environment.GetEnvironmentVariable("XAI_API_KEY") ?? "";
        o.ChatModel = NonEmpty("GROK_MCP_CHAT_MODEL", o.ChatModel);
        o.CreativeModel = NonEmpty("GROK_MCP_CREATIVE_MODEL", o.CreativeModel);
        o.FastModel = NonEmpty("GROK_MCP_FAST_MODEL", o.FastModel);
        o.MultiAgentModel = NonEmpty("GROK_MCP_MULTI_AGENT_MODEL", o.MultiAgentModel);
        o.ImageModel = NonEmpty("GROK_MCP_IMAGE_MODEL", o.ImageModel);
        o.VideoModel = NonEmpty("GROK_MCP_VIDEO_MODEL", o.VideoModel);
        o.LogLevel = NonEmpty("GROK_MCP_LOG_LEVEL", o.LogLevel);

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultLogDir = string.IsNullOrEmpty(localApp)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "logs")
            : System.IO.Path.Combine(localApp, "grok-mcp", "logs");
        o.LogDir = NonEmpty("GROK_MCP_LOG_DIR", defaultLogDir);

        if (int.TryParse(Environment.GetEnvironmentVariable("GROK_MCP_HTTP_TIMEOUT_SEC"), out var t) && t > 0)
            o.HttpTimeoutSeconds = t;
        if (int.TryParse(Environment.GetEnvironmentVariable("GROK_MCP_SESSION_CAP"), out var c) && c > 0)
            o.SessionTurnCap = c;
        if (int.TryParse(Environment.GetEnvironmentVariable("GROK_MCP_PORT"), out var p) && p > 0 && p <= 65535)
            o.ListenPort = p;
        if (int.TryParse(Environment.GetEnvironmentVariable("GROK_MCP_LOG_RETENTION_DAYS"), out var r) && r >= 0)
            o.LogRetentionDays = r;
    }

    private static string NonEmpty(string envVar, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }

    // Reads KEY=VALUE lines into process env. Caller orders files by priority; this method
    // skips any key that already exists in env, so a higher-priority source is preserved.
    internal static void LoadEnvFile(string path)
    {
        if (!System.IO.File.Exists(path)) return;
        foreach (var raw in System.IO.File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 1) continue;
            var key = line[..eq].Trim();
            if (key.Length == 0) continue;
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val[1..^1];
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
    }
}

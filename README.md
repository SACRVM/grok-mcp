# grok-mcp

Local HTTP MCP server that wraps the xAI Grok API. Built so Claude Code can:

- **Talk to Grok as a buddy** — second opinion, debugging help, design review
- **Generate, edit, and analyze images** — game graphics, app assets, etc.

One background process, many parallel Claude Code clients. Loopback-only (127.0.0.1), no auth, no public exposure.

## Tools

| Tool | What it does |
|---|---|
| `grok_chat` | Chat completion on the flagship model (`grok-4.6`). Always reasons. Stateless by default; pass `session_id` to keep an in-memory thread within the server's process lifetime. Sessions are shared across all connected clients. |
| `grok_chat_fast` | Same, on a dedicated non-reasoning model (`grok-4.20-0309-non-reasoning`, 1M context). Answers without thinking first — for lookups, classification, extraction, reformatting. |
| `grok_chat_multi_agent` | Sends a hard problem to `grok-4.20-multi-agent-0309`, where `agents` (4 or 16) work it in parallel and reconcile. Slow and token-hungry; reserve it for problems worth the cross-check. |
| `grok_generate_image` | Text → image, saved to `output_path` and returned inline so Claude sees it. Runs on `grok-imagine-image-2.0`; `quality` (`low` / `medium` / `auto`) tunes render cost. |
| `grok_edit_image` | Up to five input images + prompt → modified image, same output handling. |
| `grok_describe_image` | Vision: ask Grok to analyze image(s). Returns text. |
| `grok_generate_video` | Text (+ optional seed image) → video with an optional audio track, saved to `output_path` as MP4. Runs on `grok-imagine-video-1.5` (up to 1080p). Generation is asynchronous on xAI's side — the call polls until done and can block for minutes. Text-only result (no inline preview). |

`output_path` on the image tools must be **absolute** — Claude Code's working directory is not the server's working directory. For `n>1`, the index is inserted before the extension (`mushroom.png` → `mushroom-1.png`, `mushroom-2.png`, …). Image-input parameters accept any of: `https://…` URL, absolute file path, `data:image/...;base64,…` URI, or raw base64.

## Install (Windows)

1. Download `GrokMcpSetup-<version>-win-x64.exe` from the [Releases](https://github.com/SACRVM/grok-mcp/releases) page, or build it yourself (see [Build from source](#build-from-source)).
2. Run the installer. It will:
   - Prompt you for your `XAI_API_KEY` (get one from [console.x.ai](https://console.x.ai/) — pasted into `%LOCALAPPDATA%\grok-mcp\config.env`, never logged).
   - Install to `%LOCALAPPDATA%\Programs\grok-mcp\` (no UAC).
   - Register a Scheduled Task that starts the server on every logon and auto-restarts on crash.
   - Register the MCP with Claude Code (`user` scope) — if the `claude` CLI is on PATH.
3. Open any Claude Code session — `/mcp` should show `grok` connected with 7 tools.

Re-running the installer is safe: an existing `config.env` is preserved and the service is bounced to pick up the new build.

Requires the .NET 10 Desktop Runtime (the installer doesn't bundle it; download from [microsoft.com/dotnet](https://dotnet.microsoft.com/download/dotnet/10.0) if missing).

## Configuration

The server reads `config.env` files at startup. Priority (highest wins):

1. Real process environment variables.
2. `%LOCALAPPDATA%\grok-mcp\config.env` — written by the installer.
3. `%USERPROFILE%\.grok-mcp\config.env` — optional fallback (useful if you sync your home folder).

| Var | Default | Purpose |
|---|---|---|
| `XAI_API_KEY` | — (required, fail fast) | Bearer token |
| `GROK_MCP_CHAT_MODEL` | `grok-4.6` | default `grok_chat` model |
| `GROK_MCP_CREATIVE_MODEL` | `grok-4.6` | default vision/heavy model |
| `GROK_MCP_FAST_MODEL` | `grok-4.20-0309-non-reasoning` | model behind `grok_chat_fast`; must be one that cannot reason |
| `GROK_MCP_MULTI_AGENT_MODEL` | `grok-4.20-multi-agent-0309` | model behind `grok_chat_multi_agent` (needs the `/responses` endpoint) |
| `GROK_MCP_IMAGE_MODEL` | `grok-imagine-image-2.0` | default image gen/edit model; the only one that honours `quality` |
| `GROK_MCP_VIDEO_MODEL` | `grok-imagine-video-1.5` | default video model for text-to-video and image-to-video |
| `GROK_MCP_LOG_LEVEL` | `Information` | `Trace`/`Debug`/`Information`/`Warning`/`Error` |
| `GROK_MCP_LOG_DIR` | `%LOCALAPPDATA%\grok-mcp\logs` | rolling-file log directory |
| `GROK_MCP_HTTP_TIMEOUT_SEC` | `300` | HttpClient timeout |
| `GROK_MCP_SESSION_CAP` | `50` | max turns retained per session |
| `GROK_MCP_PORT` | `6677` | port the server binds on `127.0.0.1` |
| `GROK_MCP_LOG_RETENTION_DAYS` | `30` | older `grok-mcp-*.log` files are deleted at startup; `0` disables pruning |

Per-call tool params (`model`, `temperature`, `aspect_ratio`, etc.) override config defaults. The API key is never logged.

After editing `config.env`, restart the service so the new values take effect:

```powershell
Stop-ScheduledTask  -TaskName grok-mcp-server
Start-ScheduledTask -TaskName grok-mcp-server
```

## Verify it's running

```powershell
Invoke-WebRequest http://127.0.0.1:6677/health
# {"status":"ok","pid":12345,"version":"1.0.0.0"}
```

If the probe fails, check today's log under `%LOCALAPPDATA%\grok-mcp\logs\`. Most common: missing `XAI_API_KEY` (server exits with exit code 2).

## Build from source

Requires .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
powershell.exe -ExecutionPolicy Bypass -File installer\build-installer.ps1
# Override version:
powershell.exe -ExecutionPolicy Bypass -File installer\build-installer.ps1 -AppVersion 1.0.1
```

Output: `bin\installer\GrokMcpSetup-<version>-win-x64.exe`.

## Hot reload during development

Edit code → run one script. No need to restart Claude Code; clients reconnect automatically.

```powershell
powershell.exe -ExecutionPolicy Bypass -File scripts\dev-update.ps1
```

The script stops the Scheduled Task, waits for the process to release `grok-mcp.exe`, `dotnet publish`-es straight into the install directory, and starts the task again. The end-to-end cycle is typically under 5 seconds.

## Tests

Unit tests live in `tests\GrokMcp.Tests\` (xUnit) and cover the pure-logic services plus the HTTP and tool layers with a mocked `HttpMessageHandler`:

```powershell
dotnet test tests\GrokMcp.Tests\GrokMcp.Tests.csproj
```

CI (`.github\workflows\build.yml`) runs `dotnet format --verify-no-changes`, `dotnet test`, and a publish-and-boot smoke on every push to `main` and every PR. The release workflow runs the same tests before building the installer, so a red test blocks both a merge and a release.

Tests do not hit the xAI API — that surface is still verified manually via the smoke tests below.

## Smoke tests

After install, in a fresh Claude Code session with the MCP wired up:

1. `/mcp` — `grok` connected, 7 tools listed.
2. *"Use grok_chat to greet me in five languages."* Expect a few seconds' round-trip (`grok-4.6` reasons before answering); log shows token usage.
3. *"Use grok_chat_fast to name the capital of France in one word."* Expect "Paris" and no reasoning tokens — this is the non-reasoning path.
4. *"Use grok_chat_multi_agent with agents=4 to sanity-check <some claim>."* Expect a reconciled answer, often ending in a `\confidence{N}` marker. Slower and far more tokens than `grok_chat` — that's inherent to the model.
5. *"Use grok_chat with session_id='test1' to remember my name is Ada. Then in a separate call with the same session_id ask what my name is."* Expect "Ada". This works **across different Claude Code sessions** now — try it with two terminal windows open.
6. *"Use grok_generate_image with prompt='a tiny pixel-art mushroom on transparent background' and output_path='C:\\Users\\you\\Desktop\\grok-test\\mushroom.png'."* Expect the file on disk + Claude can describe the inline image without re-reading it.
7. *"Use grok_edit_image with images=['…\\mushroom.png'], prompt='now make it a glowing crystal mushroom', output_path='…\\crystal.png'."* Expect a recognizably-derived new image.
8. *"Use grok_describe_image on `crystal.png` and tell me its color palette."* Expect coherent description.
9. *"Use grok_generate_video with prompt='a tiny pixel-art mushroom slowly rotating', duration=1, resolution='480p', output_path='C:\\Users\\you\\Desktop\\grok-test\\mushroom.mp4'."* Keep it short (1s / 480p) — this can still take a minute or more to complete and spends real API credit. Expect an MP4 on disk.

## Project layout

```
grok-mcp\
├── GrokMcp.csproj            (Microsoft.NET.Sdk.Web, single-file publish)
├── Program.cs                (host bootstrap, HTTP transport, /mcp + /health endpoints)
├── Config\GrokOptions.cs     (env + config.env binding, both candidate paths)
├── Services\
│   ├── GrokClient.cs         (HTTP + retry; chat / images / vision)
│   ├── ChatSessionStore.cs   (in-memory session history, shared across HTTP clients)
│   ├── ImageInputResolver.cs (URL / path / data-URI / base64 → data URI)
│   └── ImageWriter.cs        (resolve path, write bytes, return paths)
├── Tools\GrokTools.cs        (the 7 [McpServerTool] methods)
├── tests\GrokMcp.Tests\      (xUnit — services, GrokClient, GrokTools)
├── installer\                (Inno Setup script + companion PowerShell)
└── scripts\                  (developer helpers — dev-update.ps1)
```

## Logs

Two sinks:

- **stderr** — every level. The Scheduled Task captures this in Event Viewer if needed.
- **rolling file** — `%LOCALAPPDATA%\grok-mcp\logs\grok-mcp-YYYY-MM-DD.log`.

Each Grok call logs the model, attempt, payload size, prompt preview (≤100 chars), and on success the `usage` token counts.

## Troubleshooting

- **`/mcp` shows red / disconnected.** First check `Invoke-WebRequest http://127.0.0.1:6677/health`. If that fails, the server isn't running — `Get-ScheduledTask -TaskName grok-mcp-server` should show `State: Ready` or `Running`. If `State: Disabled` or the task is missing, re-run the installer.
- **`HTTP 400 model not found` from a tool call.** xAI deprecates legacy aliases periodically. Set `GROK_MCP_CHAT_MODEL` / `GROK_MCP_CREATIVE_MODEL` / `GROK_MCP_IMAGE_MODEL` / `GROK_MCP_VIDEO_MODEL` in `config.env` to the current model id from the xAI console and restart the task.
- **`output_path must be absolute`.** Claude Code's working directory is not the server's working directory. Always pass a full path.
- **Port 6677 already in use.** Set `GROK_MCP_PORT` in `config.env` to a free port (e.g. `GROK_MCP_PORT=6688`), bounce the task, and re-register the MCP URL with Claude Code (`claude mcp remove grok --scope user; claude mcp add grok --scope user --transport http http://127.0.0.1:6688/mcp`).

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A local **HTTP** MCP server (.NET 10, C#, ASP.NET Core) that wraps the xAI Grok REST API. Exposes seven tools to Claude Code: `grok_chat`, `grok_chat_fast`, `grok_chat_multi_agent`, `grok_generate_image`, `grok_edit_image`, `grok_describe_image`, `grok_generate_video`. One background process binds to `http://127.0.0.1:6677` and serves many parallel Claude Code clients — loopback only, no auth.

**Tool-per-intent, not flag-per-intent.** The three chat tools exist because xAI's `reasoning_effort` is only valid in model-specific combinations (`none` works on `grok-4.3` but is rejected by `grok-4.5`; `xhigh` means "16 agents" only on the multi-agent model). Rather than push that matrix onto the calling agent, each tool hard-wires a model and exposes only the knobs that model actually honours. Keep it that way when adding models.

## Build & install

```powershell
powershell.exe -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Pipeline: `dotnet publish -c Release -o bin\win-x64` (single-file `grok-mcp.exe`, ~2.3 MB, framework-dependent on .NET 10 Desktop Runtime) → ISCC compiles `installer\grok-mcp.iss` → installer at `bin\installer\GrokMcpSetup-<version>-win-x64.exe`.

The installer is end-to-end one-shot: a custom wizard page asks for `XAI_API_KEY` (skipped if `config.env` already has one), writes `%LOCALAPPDATA%\grok-mcp\config.env`, drops `grok-mcp.exe` into `%LOCALAPPDATA%\Programs\grok-mcp\`, registers the `grok-mcp-server` Scheduled Task (on-logon, auto-restart on crash), bounces the service so the new key is loaded, and registers the MCP with Claude Code via `claude mcp add grok --scope user --transport http ...` (silently no-ops if the CLI isn't on PATH). Plain user-scope install (`PrivilegesRequired=lowest`, no UAC). The final wizard page reports what was done.

Unit tests live in `tests/GrokMcp.Tests/` (xUnit) and cover the pure-logic services: `ImageWriter`, `ImageInputResolver`, `ChatSessionStore`, and `GrokOptions.LoadEnvFile`. Run with:

```powershell
dotnet test tests\GrokMcp.Tests\GrokMcp.Tests.csproj
```

CI (`.github/workflows/build.yml`) runs these on every push to `main` and every PR; the release workflow runs them before cutting an installer. The HTTP layer and xAI integration are still verified manually via the smoke tests in `README.md` — no recorded fixtures or live-API contract tests yet. No linter.

`GrokMcp.csproj` excludes `tests\**` from its compile glob (the test files would otherwise be sucked into the main assembly because the SDK's default `**/*.cs` pattern matches them). `InternalsVisibleTo("GrokMcp.Tests")` lets tests reach `internal` members like `GrokOptions.LoadEnvFile`.

## Hot reload during development

```powershell
powershell.exe -ExecutionPolicy Bypass -File scripts\dev-update.ps1
```

The script stops the Scheduled Task, waits up to 15 s for `grok-mcp.exe` to release the file lock, `dotnet publish`-es straight into `%LOCALAPPDATA%\Programs\grok-mcp\`, then starts the task again. Claude Code clients reconnect on their own. **Do not** use plain `dotnet build -c Release` for hot reload — that writes to `bin\` and the installed copy is untouched.

There is intentionally no `BuildWatcher` or shadow-copy launcher. The Scheduled Task is the only restart mechanism.

## Architecture

- **`Program.cs`** — `WebApplication` host. Binds `GrokOptions` from `config.env` + env vars *before* building the DI container (logging level needs it). Fail-fast on missing `XAI_API_KEY` (exit 2). Registers MCP server with `WithHttpTransport(o => o.Stateless = true).WithToolsFromAssembly()`. Maps `/mcp` (the streamable-HTTP MCP endpoint) and `/health` (JSON probe with `pid`, `version`). Always binds to `http://127.0.0.1:6677` — loopback only.

- **`Tools\GrokTools.cs`** — The seven MCP tool methods. Thin wrappers that validate arguments, resolve image inputs, call `GrokClient`, and package results. The three chat tools share `RunChatAsync` (session assembly, history replay, error shaping) and differ only in the delegate they hand it: `grok_chat` → `ChatAsync` on `grok-4.5`, `grok_chat_fast` → `ChatAsync` on the non-reasoning model with `reasoning_effort` never set, `grok_chat_multi_agent` → `ResponsesAsync` with `agents` 4/16 mapped onto `high`/`xhigh`. Image tools return `CallToolResult` with both a `TextContentBlock` (saved-path summary) and an `ImageContentBlock` per saved image so the calling agent sees the bytes inline without re-reading the file. `grok_generate_video` returns `TextContentBlock` only — video isn't inlined.

- **`Services\GrokClient.cs`** — HTTP layer to xAI. Three retry attempts at delays `[0, 2s, 6s]`. Treats HTTP 5xx and 429 as transient; 4xx breaks out of the retry loop. Generation hits `/images/generations`, edits hit `/images/edits` (presence of `inputs` decides). Vision uses `/chat/completions` with multimodal `content` arrays. `ResponsesAsync` hits `/responses` and exists solely for the multi-agent model, which xAI refuses on `/chat/completions` (HTTP 400 "Multi Agent requests are not allowed on chat completions"); that endpoint takes `input` instead of `messages`, returns the text under `output[].content[].text`, and names its usage fields `input_tokens`/`output_tokens`. Video generation posts to `/videos/generations` (returns a `request_id` immediately) and is polled via `GET /videos/{request_id}` every 5s until `done`/`failed`/`expired`, up to a 10-minute timeout, before downloading the MP4 from `video.url`; the video model is auto-selected per call unless pinned (`grok-imagine-video-1.5` for image-to-video, `grok-imagine-video` for text-to-video — 1.5 rejects text-to-video with HTTP 400). Token usage logged on every chat response.

- **`Services\ImageInputResolver.cs`** — Normalizes four input shapes (`http(s)://`, `data:`, absolute file path, raw base64) into `{ url = "data:...;base64,..." }` objects for xAI's `image_url` content blocks.

- **`Services\ImageWriter.cs`** — Writes bytes to `output_path`. Enforces absolute paths (the MCP's CWD is not the agent's CWD — this is a real bug source). Sniffs PNG/JPG/GIF/WebP magic bytes and reconciles the extension: matching → kept; mismatched but known image ext (`watch.png` + JPG bytes) → **replaced** (`watch.jpg`); no ext / unknown ext → real one **appended**. For `n>1`, indexes go before the extension. `WriteVideo` applies the same absolute-path rule for MP4 bytes, sniffing the `ftyp` box at offset 4: `.mp4` kept, other known video extensions replaced, unknown/missing appended.

- **`Services\ChatSessionStore.cs`** — In-memory only. `ConcurrentDictionary<string, List<ChatTurn>>` keyed by `session_id`. With HTTP transport the server is a singleton process, so a `session_id` used by Claude Code session A is visible to Claude Code session B. Lifetime = server process. FIFO trim at `GROK_MCP_SESSION_CAP` turns.

- **`Config\GrokOptions.cs`** — Resolution order at startup: `%LOCALAPPDATA%\grok-mcp\config.env` → `%USERPROFILE%\.grok-mcp\config.env` → real process env vars (each step only sets keys not already present, so later steps win). Per-call tool params override these. Model defaults: `ChatModel`/`CreativeModel` = `grok-4.5`, `FastModel` = `grok-4.20-0309-non-reasoning`, `MultiAgentModel` = `grok-4.20-multi-agent-0309`. `FastModel` must stay a model that *cannot* reason — that property, not a flag, is what makes `grok_chat_fast` fast.

- **`installer\grok-mcp.iss`** — Inno Setup script. Style copied from `firepit-ai\installer\firepit.iss`: `{localappdata}\Programs\<Name>`, `PrivilegesRequired=lowest`, `Compression=lzma2/ultra`, `ArchitecturesAllowed=x64` (NOTE: `x64compatible` requires Inno Setup 6.3+; we target 6.2.1+).

- **`installer\install-task.ps1` / `uninstall-task.ps1`** — Called from `[Run]` / `[UninstallRun]`. Register/remove the `grok-mcp-server` Scheduled Task with `RestartCount=999, RestartInterval=1m`, run under interactive user, Limited runlevel (no elevation).

- **`installer\seed-config.ps1`** — Writes a `config.env` template to `%LOCALAPPDATA%\grok-mcp\config.env` only if the file doesn't exist (idempotent across re-installs).

## Conventions

- All Grok HTTP calls go through `GrokClient.PostWithRetryAsync`. New endpoints should use it so retry + logging stay uniform.
- New MCP tools: add a `[McpServerTool]` method to `GrokTools` (or another `[McpServerToolType]` class — auto-discovered via `WithToolsFromAssembly()`). Descriptions in `[Description]` attributes are what the agent sees; be precise about required vs optional and absolute-path requirements.
- Never log the API key. Current code only logs model name, attempt number, payload size, and a 100-char prompt preview.
- Image-output tools must accept `output_path` as **absolute** and reject relative paths with the existing error message.
- The script directory is `scripts\`, **not** `tools\` — `Tools\` already holds the C# `[McpServerTool]` namespace, and Windows is case-insensitive.
- `GrokMcp.csproj` is `<OutputType>WinExe</OutputType>` so the Scheduled Task launches grok-mcp without a visible console window. Consequence: `Console.Error`/`Console.Out` is detached when stderr/stdout aren't redirected, so anything you want a user to see when manually running the exe must also be written to the file logger or to a known path on disk. The startup `FATAL: XAI_API_KEY missing` branch in `Program.cs` already does this — follow the same pattern for new early-fail paths.
- `.ps1` files in this repo: keep ASCII-only or save with **UTF-8 BOM**. `powershell.exe` (PS 5.1) is the install/dev shell and parses BOM-less files as Windows-1252, which mangles em-dashes/arrows/umlauts into garbage and produces confusing `TerminatorExpectedAtEndOfString` parse errors.

## Wiring into Claude Code

The installer does this automatically (user-scope). Manual fallback if `claude` isn't on PATH at install time:

```powershell
claude mcp add grok --scope user --transport http http://127.0.0.1:6677/mcp
```

One registration, all projects pick it up. No per-project `.mcp.json` required. `.mcp.json.example` is kept for users who prefer per-project pinning.

## Firepit inbox

At the start of a session, read any pending messages in `.firepit/inbox/*.md` — cross-project notes Firepit routes here. Act on each, then mark it done with the `firepit_inbox_complete` MCP tool, passing the message's filename as the `id`.

## Firepit knowledge

Before researching something that may already be known, query the knowledge base with the `firepit_knowledge_search` MCP tool (scope `both` covers this project plus the global base). Save durable findings with `firepit_knowledge_add` — written in English, per the indexing convention. The created markdown files live under `.firepit/knowledge/` and are committed like any other file.

## Firepit pinned knowledge

@.firepit/knowledge-pinned.md

The import above auto-loads the knowledge docs marked `pin: true` in their frontmatter — always-on rules that apply every session without a search. Firepit regenerates the file from the pinned docs; don't edit it directly. Pin/unpin via the pinned flag on `firepit_knowledge_add` / `firepit_knowledge_update`, and keep the pinned set small — everything else stays reachable through `firepit_knowledge_search`.

## Firepit artifacts

When you produce a file the user will want to open — a report, screenshot, diagram, generated image, log excerpt, build output, or an executable you built for them to run — pin it with the `firepit_artifact_add` MCP tool so it appears in the project's paperclip pane. Do this as you produce it, not at the end of the session; a path buried in scrollback is a path the user has to hunt for. Pinning only links the file — it stays where it is, and `firepit_artifact_remove` never deletes it. Check `firepit_artifact_list` first so you update an existing entry instead of piling up near-duplicates, and unpin what has gone stale.

## Firepit conventions

<!-- claude-firepit-fragments -->

@../.firepit/projects/claude.md
@../.firepit/projects/claude-github-public.md

The two imports above are shared files in the Firepit central repo — edit them there and every project follows. They carry policy; the tools themselves are described by Firepit's MCP server at the handshake, so nothing is duplicated between the two.

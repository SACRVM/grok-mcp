# Changelog

All notable changes to grok-mcp are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [1.3.0] - 2026-09-07

Catch-up with xAI's summer releases, plus one correction that was costing
money: the 4-agent setting of `grok_chat_multi_agent` had been running 16
agents all along.

### Added
- `quality` on `grok_generate_image` / `grok_edit_image` (`low`, `medium`,
  `auto`) — honoured by the new default image model only.
- `generate_audio` on `grok_generate_video` (default `true`, xAI's default);
  pass `false` for a silent clip.
- `reasoning_effort="xhigh"` on `grok_chat` — the new flagship supports it.
- `grok_edit_image` accepts up to 5 source images (xAI raised the limit from
  3), and both image tools take the new `21:9` / `5:2` aspect ratios.
- Image calls log the model xAI reports as having served the request and the
  exact cost, next to the token line chat calls already had.

### Changed
- **Default chat/vision model is now `grok-4.6`** (was `grok-4.5`): xAI's
  recommended flagship since 2026-08-12, same input/output price, 500k context,
  `xhigh` available. `grok-4.5` stays selectable via `model`.
- **Default image model is now `grok-imagine-image-2.0`** (was
  `grok-imagine-image`): the model xAI recommends and the only one with a
  `quality` control. The older model remains selectable via `model` or
  `GROK_MCP_IMAGE_MODEL`.
- **Video runs on `grok-imagine-video-1.5` for both text-to-video and
  image-to-video.** The per-mode auto-select is gone: xAI added text-to-video
  to 1.5 on 2026-07-31, and 1.5 is the only model with 1080p.
  `GROK_MCP_VIDEO_MODEL` now defaults to it instead of empty.
- `grok_chat_multi_agent` sends the effort as the nested `reasoning.effort`
  object xAI documents for `/responses` (the flat field was only a fallback).

### Fixed
- **`grok_chat_multi_agent(agents=4)` ran 16 agents.** xAI maps `low`/`medium`
  to 4 agents and `high`/`xhigh` to 16; 1.2.0 sent `high` for 4. The API never
  rejects an effort value, so nothing errored — the calls were just 2–4x more
  expensive than advertised. `agents=4` now sends `medium`.
- Tool descriptions and docs no longer recommend `grok-imagine-image-quality`,
  which xAI retires on 2026-11-02.

## [1.2.0] - 2026-07-16

Grok's chat surface is now one tool per intent instead of one tool with a
model-dependent flag matrix. xAI only accepts `reasoning_effort` in
model-specific combinations, and encoding that in tool names removes the
guesswork from the calling agent.

### Added
- **`grok_chat_fast`** — chat on a dedicated non-reasoning model
  (`grok-4.20-0309-non-reasoning`, 1M context). Has no `reasoning_effort` and
  no `model` parameter by design: the model *cannot* reason, which is what
  makes it fast. For lookups, classification, extraction, reformatting.
- **`grok_chat_multi_agent`** — chat on `grok-4.20-multi-agent-0309` with an
  honest `agents` parameter (4 or 16) instead of xAI's `reasoning_effort:
  "xhigh"` overload. Runs against the `/responses` endpoint (new
  `GrokClient.ResponsesAsync`), which is the only endpoint that accepts this
  model.
- `GROK_MCP_FAST_MODEL` and `GROK_MCP_MULTI_AGENT_MODEL` config vars.

### Changed
- **Default chat/vision model is now `grok-4.5`** (was `grok-4.3`): stronger
  model, 500k context, always-on reasoning defaulting to `"high"`. Pass
  `model="grok-4.3"` to `grok_chat` if you need its 1M context.
- `grok_describe_image` defaults to `grok-4.5`.

### Removed
- **Breaking:** `grok_chat` no longer accepts `reasoning_effort="none"` or
  `"xhigh"`. `grok-4.5` rejects `"none"` with HTTP 400, so the tool now fails
  fast with a message pointing at `grok_chat_fast` (for `"none"`) or
  `grok_chat_multi_agent` (for `"xhigh"`) instead of spending a call.

### Fixed
- Documentation recommended reaching multi-agent via
  `grok_chat(model="grok-4.20-multi-agent-0309", reasoning_effort="xhigh")`.
  That never worked — xAI rejects the model on `/chat/completions` with HTTP
  400 ("Multi Agent requests are not allowed on chat completions"). Use
  `grok_chat_multi_agent`.
- The seeded `config.env` template named `grok-3-mini` and `grok-4-latest` as
  defaults; both models have since been retired by xAI.

## [1.1.0] - 2026-07-02

Video generation, and the move to `grok-4.3`. Backfilled on 2026-07-16 — this
release was tagged and shipped without a changelog entry at the time.

### Added
- **`grok_generate_video`** — text (+ optional seed image) → MP4, saved to
  `output_path`. xAI's video API is asynchronous: the call polls every 5s until
  `done`/`failed`/`expired`, giving up after 10 minutes. Bytes are sniffed for
  the `ftyp` box and the extension reconciled the same way `ImageWriter` does.
  The model auto-selects per mode — `grok-imagine-video-1.5` for image-to-video,
  `grok-imagine-video` for text-to-video, because 1.5 rejects text-to-video with
  HTTP 400. Pin one via `GROK_MCP_VIDEO_MODEL`.
- `reasoning_effort` on `grok_chat` (`"none"`/`"low"`/`"medium"`/`"high"`),
  serialized only when set so xAI's own default applies otherwise.
- **Sticky session caching** — with a `session_id`, an `x-grok-conv-id` header
  routes the request to the same xAI backend, so repeat-prefix tokens bill at
  the cached rate instead of full price.
- `resolution` (`1k`/`2k`) on the image tools, the full `aspect_ratio` list
  including `auto`, and `grok-imagine-image-quality` documented as an override.
- `/release` slash command.

### Changed
- Chat and vision defaults moved from `grok-3-mini` / `grok-4-latest` to
  **`grok-4.3`** (1M context, vision, function calling, configurable reasoning).
  Both old models were retired by xAI shortly afterwards.

### Notes
- This release also advertised `reasoning_effort="xhigh"` with
  `model="grok-4.20-multi-agent-0309"` on `grok_chat`. That combination never
  worked — see 1.2.0 above.

## [1.0.0] - 2026-05-10

First public release. This rebuild fixes the chronic "MCP failed" reconnect
storms that the prior stdio prototype hit whenever more than one Claude Code
session was open at the same time.

### Changed
- **Transport**: stdio replaced by HTTP. Bind defaults to
  `http://127.0.0.1:6677/mcp`, loopback only. One server process now serves
  many parallel Claude Code clients. `ChatSessionStore` is consequently shared
  across all clients — a `session_id` set from one window is visible in
  another.
- **Distribution**: stops shipping launcher scripts inside the project
  directory. New Inno Setup installer puts a single-file
  `grok-mcp.exe` (~2.3 MB, framework-dependent on .NET 10 Desktop Runtime)
  into `%LOCALAPPDATA%\Programs\grok-mcp\` and registers a per-user Scheduled
  Task `grok-mcp-server` that runs at logon and auto-restarts on crash. No
  UAC.
- **Config**: `XAI_API_KEY` and friends now live in
  `%LOCALAPPDATA%\grok-mcp\config.env` (or `%USERPROFILE%\.grok-mcp\config.env`
  as fallback). Real process env vars still override both. Removes the need
  to inline the key in every `.mcp.json` or `claude mcp add` command.

### Added
- **Wizard-driven setup**: installer prompts for the API key, pre-fills it on
  re-install so you can review or change it, validates that it looks like
  `xai-…`, and writes/updates the key line in place — preserving any
  customised model overrides in `config.env`.
- **Auto-registration with Claude Code**: installer runs
  `claude mcp add grok --scope user --transport http http://127.0.0.1:6677/mcp`
  at the end (idempotent: prior registration is removed first). Final wizard
  page reports what was done; if the `claude` CLI isn't on PATH, falls back
  to a printed manual command.
- **`GROK_MCP_PORT`** env var to change the listen port from the default 6677.
- **`/health` endpoint**: returns `status`, `pid`, `version`, `port`,
  `apiKeyConfigured` (bool — never the key itself), and `uptimeSeconds`.
  Useful for quick probes and watchdog scripts.
- **`GROK_MCP_LOG_RETENTION_DAYS`** (default 30): on startup, log files
  older than this many days are deleted from the log directory. Failures
  while deleting locked files never block startup.
- **`scripts/dev-update.ps1`**: developer hot-reload — stops the Scheduled
  Task, waits for the file lock to drop, `dotnet publish`-es straight into
  the installed location, restarts the task. Replaces the old
  `BuildWatcher` + shadow-copy launcher hack.
- **`.github/workflows/release.yml`**: GitHub Actions release pipeline. On a
  `v*` tag, builds the installer on `windows-latest` and attaches it to a
  GitHub Release.
- **`CLAUDE.md`** with project-specific guidance for Claude Code instances
  working on this repo.

### Removed
- `run-grok-mcp.cmd` and `run-grok-mcp.sh` (the stdio shadow-copy launchers).
- `Services/BuildWatcher.cs` (file-system watcher that triggered self-shutdown
  on rebuild) — the Scheduled Task handles process lifecycle now.
- Need for a per-project `.mcp.json` in every repository. The user-scope HTTP
  registration covers all projects at once. `.mcp.json.example` is kept as a
  template for users who still prefer per-project pinning.

### Fixed
- **Reconnect storms across multi-project use.** Root cause was `xcopy`-locking
  on the shared `%LOCALAPPDATA%\grok-mcp\runtime\` directory whenever two
  Claude Code instances launched at the same time. With one process for all
  clients, this race is gone.
- **`Tools/` vs `tools/` collision on Windows.** Dev helpers moved to
  `scripts/` so they don't share a (case-insensitive) directory with the
  `[McpServerTool]` C# namespace.

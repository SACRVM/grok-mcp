#!/usr/bin/env bash
# Install grok-mcp on Linux as a systemd user service and register it with Claude Code.
#
#   scripts/install-linux.sh              install, or update an existing install (this is also
#                                         the hot-reload path during development)
#   scripts/install-linux.sh --uninstall  stop and remove service, binary and the Claude Code
#                                         registration; config.env and logs are kept
#
# Everything is per-user, no sudo:
#   ~/.local/lib/grok-mcp/grok-mcp           the published single-file app
#   ~/.local/bin/grok-mcp                    symlink to it, for running it by hand
#   ~/.config/grok-mcp/config.env            XAI_API_KEY and overrides (mode 600)
#   ~/.config/systemd/user/grok-mcp.service  starts at login, restarts on crash
#   ~/.local/share/grok-mcp/logs/            rolling log files (also: journalctl --user -u grok-mcp)
#
# The API key is taken from $XAI_API_KEY when set (and written to config.env), otherwise an
# existing config.env key is kept, otherwise you are prompted for it.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
install_dir="$HOME/.local/lib/grok-mcp"
bin_link="$HOME/.local/bin/grok-mcp"
config_dir="$config_home/grok-mcp"
config_env="$config_dir/config.env"
unit_name="grok-mcp.service"
unit_path="$config_home/systemd/user/$unit_name"
# Same order the server reads them in (GrokOptions.ConfigFilePaths), highest priority first.
config_files=("$config_env" "${XDG_DATA_HOME:-$HOME/.local/share}/grok-mcp/config.env" "$HOME/.grok-mcp/config.env")

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# Value of KEY as the server would resolve it: the first non-empty assignment in the first
# config file that has one (GrokOptions.LoadEnvFile never overwrites a key once set).
config_value() {
    local f v
    for f in "${config_files[@]}"; do
        [[ -f $f ]] || continue
        v="$(awk -v k="$1" -v q="'" '
            { line = $0; sub(/^[[:space:]]+/, "", line) }
            index(line, k) == 1 {
                rest = substr(line, length(k) + 1)
                if (sub(/^[[:space:]]*=[[:space:]]*/, "", rest)) {
                    sub(/[[:space:]]+$/, "", rest)
                    c = substr(rest, 1, 1)
                    if (length(rest) >= 2 && (c == "\"" || c == q) && substr(rest, length(rest)) == c)
                        rest = substr(rest, 2, length(rest) - 2)
                    if (rest != "") { print rest; exit }
                }
            }' "$f")"
        if [[ -n $v ]]; then printf '%s' "$v"; return 0; fi
    done
    return 1
}

have_systemd_user() { systemctl --user show-environment >/dev/null 2>&1; }

# `claude` on PATH, else the copies that are usually installed but not on a non-login PATH:
# the native installer's, the old npm-local one, and the CLI the Claude desktop app ships
# (its Code tab reads the same ~/.claude.json, so a user-scope entry reaches it too).
find_claude() {
    local c
    if c="$(command -v claude 2>/dev/null)"; then printf '%s' "$c"; return 0; fi
    for c in "$HOME/.local/bin/claude" "$HOME/.claude/local/claude"; do
        [[ -x $c ]] && { printf '%s' "$c"; return 0; }
    done
    c="$(printf '%s\n' "$config_home"/Claude/claude-code/*/*/claude | sort -V | tail -n 1)"
    [[ -x $c ]] && { printf '%s' "$c"; return 0; }
    return 1
}

uninstall() {
    if have_systemd_user; then
        systemctl --user disable --now "$unit_name" >/dev/null 2>&1 || true
    fi
    rm -f "$unit_path"
    have_systemd_user && systemctl --user daemon-reload
    [[ -L $bin_link ]] && rm -f "$bin_link"
    rm -rf "$install_dir"
    say "Removed service, $install_dir and $bin_link"

    local claude
    if claude="$(find_claude)"; then
        "$claude" mcp remove grok --scope user >/dev/null 2>&1 \
            && say "Removed the user-scope MCP \"grok\" from Claude Code" \
            || say "Claude Code had no user-scope MCP \"grok\""
    fi
    say "Kept $config_dir and the logs; delete them by hand if you want them gone."
}

write_key() {
    local tmp
    mkdir -p "$config_dir"
    chmod 700 "$config_dir"
    tmp="$(mktemp "$config_dir/.config.env.XXXXXX")"
    if [[ -f $config_env ]]; then
        # Replace only the key line, keep every other setting. The key goes through the
        # environment, not argv, so it never shows up in a process listing.
        GROK_KEY="$1" awk '
            /^[[:space:]]*XAI_API_KEY[[:space:]]*=/ && !done { print "XAI_API_KEY=" ENVIRON["GROK_KEY"]; done = 1; next }
            { print }
            END { if (!done) print "XAI_API_KEY=" ENVIRON["GROK_KEY"] }' "$config_env" >"$tmp"
    else
        {
            cat <<'EOF'
# grok-mcp config - read at server startup.
# ~/.config/grok-mcp/config.env takes precedence over ~/.local/share/grok-mcp/config.env
# and ~/.grok-mcp/config.env. Process env vars override all of them.
# After editing: systemctl --user restart grok-mcp

# REQUIRED - your xAI key (no quotes, no spaces):
EOF
            printf 'XAI_API_KEY=%s\n' "$1"
            cat <<'EOF'

# Optional model overrides (defaults shown):
# GROK_MCP_CHAT_MODEL=grok-4.7
# GROK_MCP_CREATIVE_MODEL=grok-4.7
# grok_chat_fast needs a model that cannot reason:
# GROK_MCP_FAST_MODEL=grok-4.20-0309-non-reasoning
# GROK_MCP_MULTI_AGENT_MODEL=grok-4.20-multi-agent-0309
# GROK_MCP_IMAGE_MODEL=grok-imagine-image-2.0
# One video model for text-to-video and image-to-video:
# GROK_MCP_VIDEO_MODEL=grok-imagine-video-1.5

# Optional runtime tuning:
# GROK_MCP_LOG_LEVEL=Information
# GROK_MCP_HTTP_TIMEOUT_SEC=3600
# GROK_MCP_SESSION_CAP=50
# GROK_MCP_PORT=6677
EOF
        } >"$tmp"
    fi
    chmod 600 "$tmp"
    mv -f "$tmp" "$config_env"
}

ensure_key() {
    if [[ -n ${XAI_API_KEY:-} ]]; then
        write_key "$XAI_API_KEY"
        say "Wrote XAI_API_KEY from the environment to $config_env"
    elif config_value XAI_API_KEY >/dev/null; then
        say "Keeping the XAI_API_KEY already configured"
    elif [[ -t 0 ]]; then
        local key
        read -r -s -p "xAI API key (from https://console.x.ai, input hidden): " key
        echo
        [[ -n $key ]] || die "No key entered."
        write_key "$key"
        say "Wrote XAI_API_KEY to $config_env"
    else
        die "No XAI_API_KEY configured. Re-run with XAI_API_KEY=... in the environment, or interactively."
    fi
}

install() {
    command -v dotnet >/dev/null 2>&1 \
        || die "dotnet not found. Install the .NET 10 SDK (Arch: dotnet-sdk aspnet-runtime; Debian/Ubuntu: dotnet-sdk-10.0)."
    local sdks runtimes
    sdks="$(dotnet --list-sdks)"
    runtimes="$(dotnet --list-runtimes)"
    grep -q '^10\.' <<<"$sdks" \
        || die "The .NET 10 SDK is missing (found: $(cut -d' ' -f1 <<<"$sdks" | paste -sd' ')). https://dotnet.microsoft.com/download/dotnet/10.0"
    grep -q '^Microsoft\.AspNetCore\.App 10\.' <<<"$runtimes" \
        || die "The ASP.NET Core 10 runtime is missing (Arch: aspnet-runtime; Debian/Ubuntu: aspnetcore-runtime-10.0)."
    # The apphost only searches a few fixed places for the runtime; pin the one this SDK uses so
    # a ~/.dotnet install works under systemd too.
    local dotnet_root
    dotnet_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"

    ensure_key

    # Global, not local: the EXIT trap runs after this function has returned.
    stage="$(mktemp -d)"
    trap 'rm -rf "$stage"' EXIT
    say "Publishing grok-mcp $(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/GrokMcp.csproj")"
    dotnet publish "$repo/GrokMcp.csproj" -c Release -o "$stage" --nologo -v quiet

    mkdir -p "$install_dir" "$(dirname "$bin_link")"
    # --remove-destination: a running grok-mcp keeps the old inode instead of failing with
    # "Text file busy"; the restart below swaps it.
    cp -f --remove-destination "$stage"/grok-mcp* "$install_dir"/
    if [[ -e $bin_link && ! -L $bin_link ]]; then
        warn "$bin_link exists and is not a symlink; leaving it alone."
    else
        ln -sfn "$install_dir/grok-mcp" "$bin_link"
    fi
    say "Installed to $install_dir"

    local port url
    port="$(config_value GROK_MCP_PORT || echo 6677)"
    url="http://127.0.0.1:$port/mcp"

    if have_systemd_user; then
        mkdir -p "$(dirname "$unit_path")"
        cat >"$unit_path" <<EOF
[Unit]
Description=grok-mcp - xAI Grok MCP server ($url)
Documentation=https://github.com/SACRVM/grok-mcp

[Service]
Type=simple
ExecStart="$install_dir/grok-mcp"
Environment="DOTNET_ROOT=$dotnet_root"
Restart=on-failure
RestartSec=5
# Exit 2 means XAI_API_KEY is missing; restarting cannot fix that.
RestartPreventExitStatus=2

[Install]
WantedBy=default.target
EOF
        systemctl --user daemon-reload
        systemctl --user enable "$unit_name" >/dev/null 2>&1
        systemctl --user restart "$unit_name"
        say "systemd user service $unit_name enabled and (re)started"
    else
        warn "No systemd user session; start the server yourself: $install_dir/grok-mcp &"
    fi

    # With systemd, only the service's own process counts: a grok-mcp started by hand would
    # answer /health just as well while the service fails to bind the port.
    local i health="" pid=""
    for i in $(seq 1 30); do
        have_systemd_user && pid="$(systemctl --user show -p MainPID --value "$unit_name")"
        health="$(curl -fsS --max-time 2 "http://127.0.0.1:$port/health" 2>/dev/null)" \
            && [[ -z $pid || $health == *"\"pid\":$pid,"* ]] && break
        health=""
        sleep 0.5
    done
    if [[ -n $health ]]; then
        say "Healthy: $health"
    else
        warn "grok-mcp did not come up on http://127.0.0.1:$port/health within 15 s."
        have_systemd_user && journalctl --user -u "$unit_name" -n 20 --no-pager >&2 || true
        exit 1
    fi

    local claude
    if claude="$(find_claude)"; then
        "$claude" mcp remove grok --scope user >/dev/null 2>&1 || true
        if "$claude" mcp add grok --scope user --transport http "$url" >/dev/null; then
            say "Registered with Claude Code as user-scope MCP \"grok\" -> $url (via $claude)"
        else
            warn "Claude Code registration failed; run: claude mcp add grok --scope user --transport http $url"
        fi
    else
        warn "Claude Code CLI not found; register by hand: claude mcp add grok --scope user --transport http $url"
    fi
    say "Done. New Claude Code sessions list grok under /mcp with 7 tools."
}

case "${1:-}" in
    "") install ;;
    --uninstall) uninstall ;;
    -h | --help) sed -n '2,/^set -euo/{/^set -euo/d;s/^# \{0,1\}//;p}' "${BASH_SOURCE[0]}" ;;
    *) die "Unknown argument: $1 (try --help)" ;;
esac

---
description: Bump grok-mcp version, tag, push — release.yml then publishes the Inno installer to the GitHub Release.
argument-hint: [test|patch|minor|major|<x.y.z>]
---

# /release — cut a grok-mcp release

You are cutting a grok-mcp release. Follow these steps **exactly**, in order.
**Never skip the confirmation step.** Speak to the owner in German; commit messages
and tag names stay English.

The release pipeline produces one artifact from a single `v*` tag push:

- `GrokMcpSetup-X.Y.Z-win-x64.exe` — Inno Setup installer built by ISCC in CI.
  Single-file `grok-mcp.exe` is ~2.3 MB, framework-dependent on the .NET 10
  Desktop Runtime (the installer doesn't bundle it).

The release workflow attaches it to the GitHub Release it creates from the tag
and uses `generate_release_notes: true` to auto-build the release body from
commits since the previous tag.

## Argument

`$ARGUMENTS` is one of:
- (empty) — auto-detect bump from commits since the last tag
- `test` — dry-run: print the plan, do nothing
- `patch` / `minor` / `major` — force that bump
- `1.0.1` (or any `X.Y.Z`) — set this exact version

## Step 1 — Sanity checks

Run all four. If any fails, stop and report to the owner — do not proceed.

```bash
git rev-parse --abbrev-ref HEAD                                    # must be 'main'
test -z "$(git status --porcelain)" && echo clean || echo dirty    # must be 'clean'
git fetch origin main --quiet
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" && echo synced || echo behind  # must be 'synced'
test -f GrokMcp.csproj && echo found || echo missing               # must be 'found'
```

## Step 2 — Determine current state

```bash
git describe --tags --match 'v*' --abbrev=0 2>/dev/null || echo v0.0.0   # last v* tag
grep -oP '(?<=<Version>)[^<]+' GrokMcp.csproj                            # csproj version
git log $(git describe --tags --match 'v*' --abbrev=0 2>/dev/null || echo)..HEAD --format='%s'  # commits since last tag
```

If `git describe` finds no tag, treat last tag as `v0.0.0` and analyze all
commits since the start.

## Step 3 — Decide the bump

grok-mcp does **not** enforce conventional-commit prefixes (`feat:`/`fix:`/…).
Classify each commit since the last tag by reading its subject — and body if
the subject is ambiguous — into one of these buckets:

| Bucket | Examples |
|---|---|
| **feature** (user-visible new capability) | "Add `<new tool>`", "Switch to HTTP transport", "Support for X" |
| **fix** (corrects wrong behavior) | "Reconcile output extension", "Fix retry on 429", "Surface tool errors" |
| **maintenance** (no user-visible change) | "Add tests", "dotnet format check", "Refactor X", "Update CLAUDE.md" |

Then parse `$ARGUMENTS`:

| Arg | Action |
|---|---|
| empty | any **feature** commits → **minor**; only **fix** + **maintenance** → **patch**; only **maintenance** (docs/tests/refactor) → **nothing to release**, stop here and tell the owner |
| `test` | same classification as empty, but stop after the plan in Step 4 |
| `patch` | force Z+1 |
| `minor` | force Y+1, Z=0 |
| `major` | force X+1, Y=0, Z=0 (pre-2.0 — confirm twice; usually wrong) |
| `X.Y.Z` | use literally — must be greater than current |

Compute `new_version` from the **csproj `<Version>`** (not from the tag — they
may differ if a previous release wasn't tagged or vice versa).

## Step 4 — Show the plan, ask for confirmation

Print to the owner in German, exactly like:

```
Letzter Tag:          v1.0.0
GrokMcp.csproj <Version>: 1.0.0
Commits seit Tag:     7 (2 feature, 3 fix, 2 maintenance)
Vorgeschlagener Bump: minor (weil feature-commits drin sind)
Neue Version:         1.1.0
Neuer Tag:            v1.1.0
```

Then list the commits since the last tag, grouped by bucket
(feature / fix / maintenance), as a sanity check the owner can scan.

**If `$ARGUMENTS` is `test`: stop here. Do not change any files. Tell the owner
„Trockenlauf — nichts geändert."**

Otherwise: ask **„OK so? [j/n]"** and wait for the answer.
- `j` / `ja` / `y` / `yes` → continue to Step 5
- anything else → abort, change nothing

## Step 5 — Bump csproj version

Edit `GrokMcp.csproj`: change `<Version>OLD</Version>` to
`<Version>NEW</Version>`. Use the Edit tool with the full surrounding
`<Version>…</Version>` string for uniqueness.

Also update the installer fallback in `installer/grok-mcp.iss`:

```
#ifndef AppVersion
  #define AppVersion "NEW"
#endif
```

(CI overrides this via `/DAppVersion`, but keeping the fallback in sync means
a local `build-installer.ps1` run also picks up the right number.)

## Step 6 — Sanity build + test

```bash
dotnet build GrokMcp.sln --configuration Release --nologo
dotnet test tests/GrokMcp.Tests/GrokMcp.Tests.csproj --nologo --verbosity quiet
```

Catches compile errors and test regressions locally before the tag goes out.
The full test suite runs in well under a second, so the cost is negligible.
If either fails, **stop**, show the error to the owner, leave the working tree
as-is for inspection — do not commit, do not tag, do not push.

## Step 7 — Commit, tag, push

```bash
git add GrokMcp.csproj installer/grok-mcp.iss
git commit -m "release: vNEW"
git tag vNEW
git push origin main
git push origin vNEW
```

Use the standard commit-message HEREDOC pattern; include the
`Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer
per repo convention.

## Step 8 — Hand off

Tell the owner in German:

```
Release vNEW ist raus.
- Tag gepusht → release.yml läuft
- dotnet test gate vor dem Installer-Build
- Inno Setup installer wird via choco+ISCC gebaut und an Release angehängt
- GitHub Release wird automatisch erstellt mit auto-generated release notes
- CI-Status: https://github.com/SACRVM/grok-mcp/actions
```

Do **not** poll or wait for the CI run — just hand off.

## Hard rules

- Never push tags without Step 4 confirmation.
- Never use `--no-verify` or `--force` on any git command.
- Never touch `.csproj` fields other than `<Version>`.
- The `/release` invocation is the explicit user authorization for the tag push
  and the public GitHub Release that follows.
- If anything is unclear or smells wrong (e.g., no commits since last tag,
  csproj/tag version mismatch, weird state), stop and ask the owner instead of
  guessing.

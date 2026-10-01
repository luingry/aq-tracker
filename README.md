# Agent Quota Tracker (aq-tracker)

A native Windows widget that tracks the usage quotas of your AI coding agents — **Codex** and **Claude** — side by side, with local Codex usage analytics and live agent activity.

> Formerly **Codex Tracker**. The project was renamed when it stopped being Codex-only. Existing installs upgrade in place: the installer moves the app to `%LOCALAPPDATA%\Programs\Agent Quota Tracker` and the app moves your settings from `%APPDATA%\CodexTracker` to `%APPDATA%\AqTracker` on first start.

<p align="center">
  <img src="assets/screenshots/aq-tracker-detailed.png" alt="Agent Quota Tracker detailed widget showing weekly quota, analytics, model ranking, and daily usage" width="46%" />
  <img src="assets/screenshots/aq-tracker-agents.png" alt="Agent Quota Tracker compact widget showing the active agents and subagents list" width="46%" />
</p>

<p align="center"><sub>Screenshots show the v0.11.4 interface; the latest release may be newer.</sub></p>

## Supported agents

| Agent | Quota | Activity | Sign-in |
| --- | --- | --- | --- |
| Codex | Official 5h and weekly quota via the Codex CLI app-server, plus local token/cost analytics | Live agents and subagents with deep links to the chat | Uses your existing Codex CLI login |
| Claude | 5h and 7d remaining quota and resets | Live interactive Claude Code sessions and unread completions | Optional, independent OAuth sign-in from Settings |

Each agent has its own profile color and its own section in Settings.

## What it does

- Compact and detailed widget modes for an at-a-glance or full view.
- Weekly Codex quota, reset countdown, and exhaustion-risk forecast.
- Optional Claude profile with independent OAuth sign-in, 5h/7d remaining quota and resets, and a separate accent color (default `#D97757`).
- Dynamic compact display: foreground apps, active work and unread completions engage each profile. When both are engaged, Codex is on the left and Claude on the right, each showing its most restrictive permitted quota. With neither engaged and the widget focused, the last profile stays visible (Codex is the initial and dual-display fallback).
- Local token and estimated API-equivalent cost analytics for today, the active quota week, and the current month.
- Model ranking and a daily usage chart.
- Live agents and subagents with their current status, model, reasoning effort, duration, hierarchy, and a deep link to the related Codex chat.
- Live interactive Claude sessions and observed unread completions. Clicking a Claude row marks its completion read and brings an available Claude desktop window forward.
- Light/dark themes, user-selected accent color, BRL/USD display, settings, tray controls, persistent placement, and always-on-top preference.
- Full interface support for **Portuguese (Brazil)** (`pt-BR`, default) and **English (United States)** (`en-US`).

## Requirements

- Windows 10 22H2 or newer, or Windows 11.
- An authenticated Codex CLI installation. Agent Quota Tracker reads the official quota through the Codex CLI app-server.
- Claude quota is optional: connect from Settings with a browser callback or the manual `code#state` flow. Claude session activity uses local Claude Code registrations; desktop metadata and window focus are optional.
- The released app targets **.NET Framework 4.8** (`net48`).

## Install

Download and run the installer from the [latest release](https://github.com/luingry/aq-tracker/releases/latest). The installer preserves your local settings and the application detects `codex.exe` automatically when possible.

## Data and privacy

The weekly quota comes from the local Codex CLI app-server. Tokens, model ranking, charts, and costs are reconstructed from the local Codex history (`~/.codex`). No account history is uploaded by the widget.

Local-history analytics cover only the Codex data available on this Windows machine; they are not a cross-device total. Cost values are **estimated API equivalents**, not real Codex billing or an invoice.

Claude activity is read from live interactive registrations in `%USERPROFILE%\.claude\sessions` and optional session-specific desktop metadata in `%APPDATA%\Claude\claude-code-sessions`. Dead registrations and malformed JSON are ignored. A process disappearing does not create an unread completion. Confirmed session focus after completion, reactivation, a row click or marking all read clears it.

The Tracker never reads or writes Claude Code's credential file. It obtains its own OAuth tokens using PKCE and validates the login state. Tokens are encrypted with Windows DPAPI for the current user in `claude-tokens.dat` beside `settings.json` in `%APPDATA%\AqTracker`; writes and refresh-token rotation are atomic. Disconnect deletes those tokens and the cached Claude quota. The Tracker sends only OAuth requests and authenticated quota requests to Claude/Anthropic endpoints; session history is not uploaded. Tokens, authorization codes and headers are not written to logs.

Claude usage is polled every 60 seconds and on engagement when older than 60 seconds, with a maximum ten-minute backoff for rate limiting or server failures. `claude-quota.json` stores the latest snapshot, shown as out of date after restart. The profile can be disabled in settings without changing Codex analytics.

## Development

```powershell
dotnet build .\AqTracker.sln
dotnet run --project .\tests\AqTracker.Tests\AqTracker.Tests.csproj
.\scripts\finalize-build.ps1
```

The generated installer is `artifacts\AqTracker-latest.exe`.

## License

[MIT](LICENSE).

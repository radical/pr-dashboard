# CI Health dashboard — v1 design

Status: approved design, pre-implementation
Date: 2026-06-29

## Summary

Add a **CI Health** tab to the PR dashboard. It surfaces, read-only, the
GitHub Actions health of the watched repositories: a daily "what's on fire"
pulse, a weekly trend, and the open bot/automated PRs with their CI status.

A single in-process background service computes the data on a cadence and
writes **shared JSON snapshots to Blob storage**, so every visitor reads the
same pre-computed report instantly — no per-user generation cost.

This is the read-only first slice of a larger vision (autonomous reaction to
broken CI: open an issue, assign a Copilot agent, drive the PR to completion).
The reaction engine is explicitly **out of scope for v1**, but the design
leaves a clean seam for it (see "Reaction-engine seam").

## Goals

- A new tab showing CI health across all watched repos, always-on and shared.
- Daily pulse (rolling 36h) recomputed hourly.
- Weekly health (rolling 7d) recomputed daily.
- A list of currently-failing workflows.
- A list of open bot/automated PRs with their CI status.
- Deploys with the existing app; no new external service; no new data source
  beyond the GitHub API the app already uses.

## Non-goals (v1)

- No automated actions: no opening issues, no assigning agents, no driving PRs.
- No Azure DevOps builds and no ciinsights/test-flakiness analysis (those data
  sources aren't reachable from the deployed web app).
- No new infrastructure (no separate worker container).
- No deep flaky-vs-real detection — only a simple streak heuristic.

## Context: what we're reusing as reference, not runtime

The `~/dev/prompts` repo already contains the thinking this builds on:

- `aspire-ci-pulse.prompt.txt` — daily 36h "what's on fire".
- `aspire-ci-health-weekly.prompt.txt` — weekly 7d trend + triage.
- `aspire-bot-shepherd.prompt.txt` — bot/automated PR detection rules.
- `AspireAiMonitor` — a local single-shot .NET orchestrator (launchd timer,
  SQLite ledger, dispatches `gh agent-task`, opens issues, drives bot PRs).

`AspireAiMonitor` runs **locally** and writes **local SQLite**; the dashboard
is a **deployed, always-on, multi-user** Azure web app. A deployed app can't
read a laptop's SQLite, so we do **not** depend on the monitor at runtime.
Instead, the prompts + monitor are the **reference spec** for *what* to compute
and *what rules* to apply. The dashboard computes its own data from the GitHub
API it already has access to.

## Architecture

```
PeriodicTimer (hourly)                 Blob: github-cache container
   │                                      ci-health/pulse.json
   ▼                                      ci-health/weekly.json
CiHealthProducer (BackgroundService)  ──► (shared snapshots, server-written)
   │   fetch GH Actions runs (server token)        │
   │   classify bot/automated open PRs             │  read
   │   compute pulse / weekly / failing-now        ▼
   │                                   GET /api/ci-health  (public)
   └─ writes snapshot ───────────────────────────► │
                                                    ▼
                                       CiHealthView (React tab, Layout A)
```

The producer mirrors the existing `GitHubPublicCacheWarmupService`: iterate the
server-configured repositories, use the server/public-cache token, be
rate-limit aware, run under the single-replica pinning the app already enforces.

### Why in-process, not a separate service

The "generated once, served to everyone" property comes from **writing the
result to shared Blob**, not from process isolation — the app already proves
this with `GitHubPublicCacheWarmupService` (shared public cache) and
`NotificationDetectorService` (single-writer Blob state).

Read-only ingestion is light (periodic fetch → compute → write). A separate
worker only earns its infra cost when the **reaction engine** lands — that
workload (dispatching agents, driving PRs) is long-running and crash-prone and
does not belong in the web tier. So: in-process now; extract a worker when the
first real action is added.

## Cadence

One background service, two cadences:

- **Pulse** (36h window): recomputed **hourly**.
- **Weekly** (7d window): recomputed **daily** — on each hourly tick, recompute
  only if the weekly snapshot is older than ~24h.

A short startup delay (as in the existing detector) lets the public cache warm
before the first cycle.

## What the producer computes (GitHub Actions only)

For each configured repo, for each **followed workflow** (see "Workflow
selection"):

**Pulse snapshot (`ci-health/pulse.json`)** — rolling 36h:
- run count, pass count, pass rate
- run-by-run result sequence (for the bar)
- failing-now entries: workflow, repo, failing-since, consecutive-failure
  streak, last-run URL, `linkedIssue` (nullable; v1 leaves null)
- flaky-vs-real hint: streak ≥ `StreakThreshold` ⇒ "real"; single failure ⇒
  "maybe flaky"

**Weekly snapshot (`ci-health/weekly.json`)** — rolling 7d:
- 7d pass rate
- prior-week (days 8–14) pass rate and delta
- per-day pass-rate buckets (for the sparkline)

Both snapshots carry an `updatedAt` timestamp.

### Bot/automated PR block

Open PRs across the configured repos, classified with the **shepherd rules**
(source: `aspire-bot-shepherd.prompt.txt`):

1. Take `author.login`, strip any leading `app/` prefix.
2. Include if the stripped login is in the configurable **bot allowlist**
   (`dependabot`, `dotnet-maestro`, `github-actions`, `aspire-repo-bot`,
   `aspire-winget-bot`, `aspire-homebrew-bot`) **OR** `author.is_bot == true`.
3. **Always drop** the two Copilot apps (`copilot-swe-agent`,
   `copilot-pull-request-reviewer`) after the test above.
4. Drop any PR carrying the `NO-MERGE` label.
5. Drop PRs carrying the `automated` label (the orchestrator's own artifacts).
6. **Union in** any PR carrying `automation-broken` (regardless of author).

For each resulting PR, show its CI status (passing / failing / pending) using
the dashboard's existing check-status path. The producer reads the already
**warmed** open-PR data for the configured repos, so this adds no new per-user
fetching.

## Storage & API

- **Blob**: two snapshots in the existing `github-cache` container
  (server-written, never user-written; the `clear-cache` command may clear
  them — acceptable, they rebuild next cycle):
  - `ci-health/pulse.json` — the hourly snapshot: pulse metrics, the
    `failingNow` list, and the `botPrs` list (all recomputed hourly, since both
    failing state and PR CI status change frequently).
  - `ci-health/weekly.json` — the daily snapshot: weekly trend metrics.
- **API**: `GET /api/ci-health` assembles the two snapshots into
  `{ pulse, weekly, failingNow, botPrs, updatedAt: { pulse, weekly } }`
  where `pulse`, `failingNow`, and `botPrs` come from `pulse.json` and `weekly`
  comes from `weekly.json`.
- **Visibility**: **public**, served to everyone from the shared snapshot — no
  login, no per-user token, mirroring the public-cache model. Gated by
  `CiHealth:Enabled`; when disabled the endpoint returns empty and the producer
  no-ops (so local dev without config keeps working).

## Frontend

Layout **A — stacked sections** (validated via mockup). Five blocks in order:

1. **Status strip** — overall verdict, repos green, workflows failing, and when
   each snapshot last updated (pulse hourly, weekly daily).
2. **Failing workflows now** — repo, workflow, failing-since, streak, last-run
   link, linked issue (null in v1), and a **ghosted "Launch fix · later"**
   affordance marking the future action seam (not wired in v1).
3. **Daily pulse (36h)** — per-workflow runs, pass rate, run-by-run bar.
4. **Weekly health (7d)** — pass rate, delta vs prior week, sparkline.
5. **Bot/automated PRs** — classified PRs with their CI status.

Changes:
- `types.ts`: extend `DashboardMode` with `'ci-health'`; add CI-health response
  types.
- `CiHealthView` component (the five blocks).
- Nav: add a "CI health" entry to the desktop nav and `MobileNav` `MODES`.
- `routing.ts`: `parseDashboardMode` accepts `ci-health`; mode is URL-routed
  like the others.
- One fetch to `/api/ci-health`.

## Configuration (`CiHealth` options section)

- `Enabled` (bool)
- `Repositories` — defaults to the warmup `Repositories` list
- `Workflows` — optional per-repo allowlist of workflow names/files to follow
- `BotLogins` — bot allowlist (defaults to the shepherd list above)
- `PulseWindowHours` (default 36), `WeeklyWindowDays` (default 7)
- `PulseRefreshMinutes` (default 60), `WeeklyRefreshHours` (default 24)
- `StreakThreshold` (default e.g. 3) — failures-in-a-row classified "real"

## Workflow selection

Per-repo **allowlist** of workflows to follow. When a repo has no explicit
list, default to the workflows that run on **push/PR to the default branch**
(filters out scheduled/maintenance bots). This keeps health signal high and
noise low, and is configurable per repo via `CiHealth:Workflows`.

## Reaction-engine seam (designed, not built)

To make the later autonomy bolt on without re-architecting:

- The failing-workflow snapshot entry reserves a nullable `linkedIssue` and a
  stable workflow identity (repo + workflow + last-run id).
- The UI shows the ghosted "Launch fix" affordance where the action lives.
- **Extraction trigger** (documented): when the first real action is added
  (open issue / dispatch `gh agent-task` / drive PR), extract the producer into
  a dedicated Aspire worker project — that long-running, crash-prone workload
  should not share the web process — and relax the single-replica pinning via
  single-leader election if needed.

## Testing

The metric math is pure and the highest-value thing to test:

- Windowing (36h / 7d / prior-7d bucketing) from a fixed set of fake runs.
- Pass-rate computation.
- Streak / flaky-vs-real classification (e.g. "4 failures in a row ⇒ real, not
  flaky"; "1 failure ⇒ maybe flaky").
- Weekly delta vs the prior 7d window.
- Bot/automated classifier: `app/` strip, allowlist OR `is_bot`, Copilot drop,
  `NO-MERGE` drop, `automated` drop, `automation-broken` union-in.

The cycle is exposed `internal` (like `NotificationDetectorService.RunCycleAsync`)
so tests drive it directly with fakes, in the existing `pr-timeline-app.Tests`
project.

Named falsifiable check: revert the streak classifier and the
"4-in-a-row ⇒ real" test must go red.

## Open follow-ups (post-v1, not in this spec)

- Bot/automated PR *lifecycle* tracking (drive to merged/closed), per the
  shepherd prompt.
- Issue-linking for failing workflows (`linkedIssue`).
- The reaction engine (worker extraction, rules, agent dispatch).
- Azure DevOps + ciinsights data sources.
- Layout tweaks (per-repo collapse, repo×workflow matrix view).

# CI Health + Bots page — triage redesign

Status: approved design, pre-implementation
Date: 2026-06-30
Follows: `2026-06-29-ci-health-dashboard-design.md` (the v1 read-only dashboard)

## Summary

Re-prioritize the **CI Health** and **Bots** pages so a leader's attention
lands on the few things that actually matter, in the shared visual language
the Review page already uses (tone-tinted rows, pill badges, attention cards).

Two changes:

1. **CI Health** becomes a single ranked *Failing now* table where
   push-triggered rolling builds and curated always-show lanes are the **top
   tier** (pinned, brightest), each failing lane shows its **per-run pass/fail
   history as a sub-row** with a derived **pattern** label, and the four old
   trend tables collapse into one de-emphasized block.

2. **Bots** becomes a single unified *Needs attention* table organized around
   the one decision that matters — **who picks this up: a 🤖 agent or a 🧑
   human** — folding bot PRs, auto-issue clusters, and (new) unlinked top-tier
   CI breaks into one ranked list.

Both are **frontend-only** changes reusing data already sent to the client.
No backend, model, or storage changes.

## Goals

- Make the page hierarchy signal-driven: the loudest thing on screen is the
  most important broken thing, not the first table.
- Give the leader the *story* of a failure at a glance (newly broken vs
  chronic vs flaky) without reading prose or clicking through.
- Reuse the Review page's tone/pill/card vocabulary so CI + Bots feel like the
  same app.
- Keep everything working offline (no dependency on the LLM shepherd/triage
  having run), enriched by those layers when present — mirroring the existing
  deterministic `botBuckets` + shepherd-enrichment pattern.

## Non-goals

- No backend, snapshot, or data-model changes. (If a field turns out to be
  missing client-side, prefer joining to an existing snapshot over adding one.)
- No real "dispatch to Copilot" action. Any dispatch affordance is **display
  only** for this round; wiring it is a separate follow-up.
- No new importance configuration. Top tier is derived from existing
  `section` + `alwaysShow`, not a new config knob.
- No change to how pulse/weekly/triage/shepherd snapshots are computed.

## Data already available (no backend work)

Verified against the current client model (`frontend/src/types.ts`) and view
(`frontend/src/components/ci/CiHealthView.tsx`):

- `WorkflowPulse.sequence: RunRef[]` — the per-run pass/fail history, newest
  first. Already rendered as the `RecentRuns` sparkline in the pulse table.
- `WorkflowPulse.section` (`"main"` | `"scheduled"`) and
  `WorkflowPulse.alwaysShow` — present per lane.
- `FailingWorkflow` carries `repository`, `lane`, `section`, `streak`,
  `failingSince`, `lastRunId/Url`, `likelyReal`, `linkedIssue`, `cadenceMinutes`.
- Pulse and failing lanes share the `${repository}\n${lane}` key, so the view
  can join a failing lane to its full pulse row (for `sequence` + `alwaysShow`)
  exactly the way it already joins to `WorkflowWeekly`.

Backend section semantics (`pr-timeline-app.Server/CiHealthLane.cs`): `"main"`
= a configured main workflow on a tracked push branch (the rolling builds);
`"scheduled"` = any other scheduled workflow. `alwaysShow` is the curated
"keep visible" flag on scheduled lanes. So:

> **top tier** ≔ `section === "main" || alwaysShow`

## CI Health page — Option 2 (priority table + blocks sub-row)

Top to bottom:

### 1. Health strip

Same strip, but the count splits top-tier from the rest:

- `🟢 CI: all lanes green at tip` when nothing is red, else
  `🟡 CI: N lane(s) red at tip` with a sub-label `K top-tier · M other`.
- Keeps the existing pulse/weekly relative-time meta + refresh button.

### 2. Failing now — one ranked table

Columns: marker · **Lane** · Repo · Failing · Assessment.

- **Row ordering:** top-tier failing lanes first, then others. Within a group,
  by `likelyReal` then `streak` (most-established breaks first).
- **Tint:** top-tier rows use the accent (purple) tint + a `TOP` pill and a
  3px accent left-border; other failing rows use danger (or warning for
  not-`likelyReal`) tint + the existing 🔴 marker.
- **Blocks sub-row:** directly under each lane row, a row that **spans the
  table** (not a new column) showing the per-run blocks from the joined
  `sequence` plus the pattern pill. Label `Recent runs`, oldest→newest.
- **Assessment cell:** unchanged — the existing triage verdict pill +
  summary + suggested action (`VerdictCell`), `⏳ analysing…` until a verdict
  for that run exists.

### 3. Trends & healthy lanes — one collapsed block

The four current tables (36h main, 36h scheduled, 7d main, 7d scheduled)
merge into a single `<details>`, collapsed by default, summarized as
`🟢 N lanes green at tip · 7d pass X%`. Expanded, it shows one trend table
(repo · lane · 7d daily blocks · 7d pass · vs prior). Degrading lanes
(negative delta) sort to the top of the expanded table.

### Pattern label (derived client-side from `sequence`)

A single pill characterizing the recent run pattern. Proposed thresholds
(tunable constants, to live beside the classifier):

- `chronic` — tip failing and trailing red streak ≥ 5 (a standing break).
- `newly red` — tip failing, red streak ≤ 4, preceded by a mostly-green run
  (a fresh break — highest urgency).
- `flaky` — many pass↔fail transitions and a moderate fail rate (intermittent).
- `recovering` — tip green after a recent red stretch (used in the trends /
  healthy view, not in Failing now where the tip is red by definition).

The label is advisory and sits alongside — not in place of — the LLM triage
verdict in the Assessment cell.

## Bots page — Option 1 (unified priority queue)

One table titled **Needs attention**.

Columns: actor (🤖/🧑) · **Item** · Repo · Why (pill) · Action · Age, with a
context sub-row (failing checks, conflict file, linked issue, or "from CI
Health").

Rows are the union of, ranked by severity then age:

1. **Bot PRs** in the `stuck` / `broken` buckets — from the existing
   deterministic `bucketBotPrs`, enriched by the shepherd's per-PR
   `why` / `action` / `bucket` when it has run.
2. **Auto-issue clusters** — one row per `BotIssueGroup` (theme, severity,
   recommendation), or per raw issue until the shepherd has clustered them.
3. **Unlinked top-tier CI breaks** *(new tie-in)* — a failing **top-tier**
   lane with `likelyReal && linkedIssue === null` becomes a 🤖 row:
   "open tracking issue + investigate", context "from CI Health · <pattern> ·
   N builds". This connects the CI page to the bot work surface and is a small
   first step toward the v1 spec's "reaction-engine seam".

**Actor (🤖/🧑):** from the shepherd's `humanOnly` for PRs; issue clusters
default 🧑; unlinked CI breaks default 🤖. The actor column is the leader's
primary filter.

**Calm (collapsed):** ready-to-merge (`easy-win`) and `pending` bot PRs go in
a collapsed `<details>` ("also on the Review page's bots lane"), as today.

**Dispatch:** out of scope — display only if shown at all; not wired here.

## Components / where the code goes

All under `frontend/src/components/ci/` + a small util, following the existing
split:

- `CiHealthView.tsx` — replace the five stacked tables with: strip,
  `FailingNowTable` (rows + block sub-rows), `TrendsDetails`.
- `BotsView.tsx` — replace the queue + bucket cards + issue cards with one
  `NeedsAttentionTable` + a `CalmDetails`.
- `ciPattern.ts` (new) — pure `classifyPattern(sequence): PatternKind` +
  thresholds, with unit tests (mirrors `botBuckets.ts` + its test file).
- A small builder that assembles the unified bot rows from
  `botPrs` + `issueGroups` + `failingNow` (deterministic; shepherd-enriched),
  also unit-tested.
- `App.css` — extend the existing `ci-*` / `attention-*` classes; add the
  tinted-row, sub-row, `TOP`/actor/pattern pill rules. No new design tokens.

## Testing

- `ciPattern` classifier: table-driven tests for each `PatternKind` incl.
  boundaries (streak = 4 vs 5; all-green; all-red; alternating). Each case
  names the sequence and the expected label.
- Unified bot-row builder: a failing top-tier lane with no linked issue yields
  a 🤖 row; with a linked issue it does not; PRs land in the right
  severity/actor; shepherd enrichment overrides the deterministic bucket.
- Existing `CiHealthComputerTests` stay green (no backend change).
- Manual: run the app against the dev snapshot, verify the join produces blocks
  for failing lanes, top-tier pinning, collapsed trends, and the bots table —
  capture commands + observed output in the PR.

## Risks / call-outs

- **Join correctness:** a failing lane must find its pulse row for the blocks
  and `alwaysShow`. If a lane is in `failingNow` but missing from
  `pulse.workflows`, render the row without blocks (graceful) rather than drop
  it.
- **Pattern thresholds are heuristic** and will need tuning against real data;
  they're isolated constants so tuning is a one-line change.
- **Bots determinism:** the unified table must render usefully before the
  shepherd runs (it often hasn't in prod, which has no CLI). The deterministic
  builder is the source of truth; shepherd output only enriches.

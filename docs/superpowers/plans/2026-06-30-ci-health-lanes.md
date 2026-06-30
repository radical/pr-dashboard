# CI Health — lane-based evolution (plan)

Status: proposed, awaiting approval (plan-first)
Date: 2026-06-30
Builds on: `docs/superpowers/specs/2026-06-29-ci-health-dashboard-design.md`

Reference targets (real dashboards, produced locally by LLM prompts):
- CI Daily Pulse — microsoft/aspire#18232
- Bot shepherd — microsoft/aspire#18285

## Why

The v1 tab groups by raw **workflow**, so `ci.yml` blends push-to-main and PR
runs into one number. The real pulse organizes by **lane** = (workflow ×
trigger), tracks **green-at-tip** (latest run state) distinctly from the window
pass rate, shows **Δ vs the 7-day average**, and uses **clean lane names**. This
plan closes those four gaps using only the GitHub Actions data we already fetch,
and records a sequenced roadmap for the richer (non-GH-Actions) features.

## In scope (this branch)

The tab is organized into **two per-repo CI sections** plus bot tracking:

1. **Main CI section** — the repo's main workflow(s) on tracked push branches.
   Per-repo `CiHealth:Lanes[repo]`: `MainWorkflows` (cleaned names, e.g.
   `["CI"]` for aspire's ci.yml; empty ⇒ all workflows) + `Branches` (exact or
   trailing-`*` glob, e.g. `["main", "release/*"]`). A push to `release/13.4` →
   lane `CI · release/13.4`.
2. **Scheduled section** — every `schedule`-triggered workflow, minus a per-repo
   `SkipScheduled` list. PRs, feature-branch pushes, and dispatch runs are
   dropped.
3. Each lane is tagged with its **section**; the 36h pulse and the 7d weekly
   trend both render Main and Scheduled as separate blocks.
4. **Green-at-tip** per lane + an "N lane(s) red at tip" headline; **Δ vs 7-day**
   on the pulse; **clean lane names**.
5. **Bot tracking** — a global bot-id list drives a bot-PR block (now showing
   each PR's **CI / mergeable / review** state via the GraphQL fetch, per
   microsoft/aspire#18285) and a bot-**issues** block. Bot rows show the title.

## Out of scope (roadmap — tracked, not built here)

Recorded so these aren't lost; each becomes its own spec/plan when picked up:

- **Bot-PR check status** — fetch per-PR checks so bot PRs show `MERGEABLE` /
  `N green` / `CONFLICTING` instead of `unknown` (the shepherd's per-PR state).
- **Auto-opened issue tracking** — recurring-break grouping (shepherd's issues
  section), needs issue enumeration + de-dup.
- **AzDO "Internal" lane** — needs Azure DevOps auth + client (not reachable
  from the deployed app today).
- **Rerun / flaky-tax + infra-share** — needs ciinsights-style data the deployed
  app can't reach.
- **Narrative / action-priority prose** — needs an LLM step in the producer.

These stay deferred because the deployed web app can't reach ciinsights/AzDO and
has no LLM in the loop, unlike the local prompts that generate #18232 / #18285.

## Design

### Lane derivation

`WorkflowRun` already carries `Event` and `HeadBranch`. Add two computed fields,
filled by the producer (which knows the repo's default branch):

- `LaneTrigger Trigger` — enum `{ Main, PullRequest, Scheduled, Other }`
- `string Lane` — display key, e.g. `"CI · main"`, `"CI · PR"`,
  `"Outerloop Tests · scheduled"`

Classification (pure, given `defaultBranch`):

```
pull_request            -> PullRequest  (label "PR")
schedule                -> Scheduled    (label "scheduled")
push & head==default    -> Main         (label "main")
otherwise               -> Other        (dropped)
```

Lane label = `$"{CleanName(workflow)} · {triggerLabel}"`.

`CleanName`: if the name contains `/`, keep the segment after the last `/`; strip
a trailing `.yml`/`.yaml`; strip a trailing `.lock`. (`.github/workflows/
analyze-ci-failure.lock.yml` → `analyze-ci-failure`.)

The default branch comes from one `GET /repos/{owner}/{repo}` call per repo per
cycle (cheap), exposed as `GitHubClient.GetDefaultBranchAsync`.

### Model changes

- `WorkflowRun`: add `string Lane`, `LaneTrigger Trigger` (producer-filled).
- `WorkflowPulse`: add `string Lane`, `bool GreenAtTip` (drop nothing; `Workflow`
  stays as the cleaned display name, `Repository` unchanged).
- `WorkflowWeekly`: add `string Lane` (so the frontend can join pulse↔weekly).
- `FailingWorkflow`: add `string Lane` (display the lane, not the raw workflow).
- The computer groups by `(Repository, Lane)` instead of `(Repository, Workflow)`.
  `GreenAtTip = IsPass(latest decided run)`.

`Δ vs 7d` is **not** stored — the API/frontend joins a pulse lane to its weekly
lane by `(repository, lane)` and renders `pulse.passRate − weekly.passRate`.

### Frontend

- Pulse + weekly + failing rows show the **lane** label.
- Status strip headline: `N lane(s) red at tip` (from `GreenAtTip`), keeping the
  pulse/weekly timestamps.
- Each failing/pulse row shows a tip dot (🟢/🔴) from `GreenAtTip`.
- Pulse row adds a `Δ7d` column: `▲/▼ {abs%}` from the join (or `—` when no
  weekly lane match yet).

## Tasks

### Task 1 — Lane model + classifier + naming (TDD)
- Add `LaneTrigger` enum + `WorkflowLane.Classify(event, headBranch, defaultBranch)`
  and `WorkflowLane.CleanName(workflow)` (pure).
- Tests: PR/schedule/main/other classification; default-branch push → main;
  non-default push → other; name cleaning for path/`.lock.yml`/plain names.
- Add `Lane`/`Trigger` to `WorkflowRun`; `Lane`/`GreenAtTip` to `WorkflowPulse`;
  `Lane` to `WorkflowWeekly` and `FailingWorkflow`.

### Task 2 — Computer groups by lane + green-at-tip (TDD)
- Change `GroupByWorkflow` → group by `(Repository, Lane)`.
- Emit `GreenAtTip` on each pulse lane; carry `Lane` everywhere.
- Update `CiHealthComputerTests` (runs now carry `Lane`); add a green-at-tip test
  ("latest decided run passed ⇒ GreenAtTip true even if window has failures") and
  a two-lane test ("same workflow, push-main vs PR ⇒ two lanes").

### Task 3 — Producer: default branch + lane enrichment
- `GitHubClient.GetDefaultBranchAsync(repo, ct)` (+ DTO, registered in context).
- In both cycles: fetch default branch once, map each fetched `WorkflowRun` to set
  `Trigger`/`Lane` via `WorkflowLane`, drop `Other`, then compute. Replace the
  current `FilterWorkflows` event filter with the lane filter (still honoring the
  per-repo workflow allowlist by cleaned name).

### Task 4 — API/frontend lanes + green-at-tip + Δ7d + naming
- Types: add `lane`, `greenAtTip` (pulse), `lane` (weekly/failing).
- `CiHealthView`: lane labels; headline `N red at tip`; per-row tip dot; pulse
  `Δ7d` column via a `(repo,lane)→weekly` map; cleaned names everywhere.

### Task 5 — Verify with `aspire run` + Playwright
- Re-run against `microsoft/aspire` + `microsoft/dcp`; confirm `CI · main` and
  `CI · PR` appear as separate lanes, green-at-tip headline is correct, Δ7d shows,
  and the `.lock.yml` noise is gone. Screenshot.

## Validation

- Each pure change is TDD'd. Named falsifiable check: revert lane grouping and the
  "same workflow, push-main vs PR ⇒ two lanes" test goes red.
- Full build + 224+ tests + frontend lint/build, plus a live `aspire run` smoke.

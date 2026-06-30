# CI workflow data → LLM analysis (plan)

Status: proposed, not yet built (plan-first)
Date: 2026-06-30
Builds on:
- `docs/superpowers/specs/2026-06-29-ci-health-dashboard-design.md`
- `docs/superpowers/plans/2026-06-30-ci-health-lanes.md`

Reference targets (real dashboards, produced locally by LLM prompts):
- CI Daily Pulse — microsoft/aspire#18232
- Bot shepherd — microsoft/aspire#18285

## Why

The CI health page today shows **what** the numbers are (per-lane pass rate,
green-at-tip, 7d trend) but not **what they mean**. The reference dashboards add
a narrative layer an LLM produces from the same run data: "X has been red for 3
days, all on the same step", "Y is flaky — passes on rerun ~60% of the time",
"these three failures share a root cause". We want that narrative in the app, but
the producer has no LLM in the loop today and the deployed app can't reach the
richer ciinsights/AzDO data the local prompts use.

This plan covers **only the data-gathering half**: assemble, from the GitHub
Actions data we already fetch (plus a small set of cheap additions), a structured
**analysis payload** that a later LLM step can consume. The prompt-execution and
rendering are out of scope here and tracked as follow-ups.

## In scope (this plan)

1. **Define the analysis payload shape** — a serializable record the producer can
   assemble per cycle and hand to a (future) prompt step. It is a superset of
   what the pulse/weekly snapshots already carry, plus the extra signals below.
   It is computed, never hand-written, so it stays in sync with the snapshots.

2. **Gather the raw signals we already have, in structured form:**
   - Per lane (workflow × trigger × section): pass rate, pass/total counts,
     green-at-tip, the newest-first run sequence (`RunRef`: pass/fail + run id +
     url), failing-since timestamp + streak for red-at-tip lanes, 7d pass rate,
     prior-7d pass rate, delta, and the per-day pass-rate buckets.
   - Per repo: default branch, tracked branches, the lane config (main workflows,
     skip/always-show scheduled).
   - Bot activity: open bot PRs with CI/mergeable/review state, open bot issues.

3. **Gather a small set of cheap additions** that materially improve analysis and
   are reachable from the deployed app's GitHub token:
   - **Per-run failed-step / job names.** Today we only keep pass/fail per run.
     The job/step that failed is the single highest-signal field for "same step
     every time" vs "different step each time" — fetch run jobs for red runs only
     (bounded: red-at-tip lanes, last N runs) to keep the request budget small.
   - **Rerun signal.** A run's `run_attempt` > 1 (or a later attempt flipping
     fail→pass) is the cheapest flakiness proxy we can get without ciinsights.
   - **Event/actor** already on the run (push vs schedule vs dispatch; who
     triggered) — already fetched, just carry it into the payload.

4. **Assemble + persist the payload** alongside the existing snapshots
   (`CiHealthSnapshotStore`), refreshed on the same cadence, so the future prompt
   step and any debugging/inspection can read a stable artifact. Expose it behind
   a guarded route (or reuse `/api/ci-health` with an opt-in query) for local
   inspection — **no LLM call yet**.

## Out of scope (follow-ups — tracked, not built here)

Each becomes its own spec/plan when picked up:

- **The prompt step itself.** Calling an LLM from the producer (or a dedicated
  worker) with the payload, parsing its narrative/finding output, and persisting
  it next to the snapshot. Needs a model endpoint + credentials in the deployed
  env, a token/latency budget, and failure-mode handling (the page must degrade
  to the numbers-only view when the LLM is unavailable).
- **Rendering the analysis.** A narrative/findings block on the CI health page
  (and possibly the Bots page) showing the LLM's per-lane and cross-lane prose,
  with provenance back to the runs it cited.
- **Richer signals the deployed app can't reach today** (carried over from the
  lanes plan): full test-failure detail, flaky-tax/infra-share, AzDO "Internal"
  lane. These need ciinsights/AzDO access the web app doesn't have.

## Notes / constraints

- **Request budget.** The per-run job/step fetch is the only new GitHub call and
  is the main cost risk. Bound it hard: red-at-tip lanes only, last N runs only,
  and rely on the shared response cache. Measure before widening.
- **Keep the payload computed + pure.** The assembly should be a pure function of
  the fetched runs + config (like `CiHealthComputer`), so it is unit-testable
  without a live GitHub client and can't drift from the snapshots it mirrors.
- **No LLM coupling in the data layer.** The payload must be useful and
  inspectable on its own; the prompt step consumes it but the data layer never
  depends on the prompt step. This keeps the numbers-only page working if/when
  the LLM step is disabled or fails.
- **When the reaction engine lands** (open issue / dispatch agent / drive PR, per
  the producer's v1 note), extract the producer + this payload assembly into the
  dedicated worker project rather than growing the web server.

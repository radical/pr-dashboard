import type { CiTriageItem, FailingWorkflow, RunRef, WorkflowPulse, WorkflowWeekly } from '../../types';
import { formatAge } from '../../utils/format';
import { buildRepoLabeler, delta, percent, relativeTime } from './ciFormat';
import { classifyPattern, describePattern, pickRunSequence } from './ciPattern';
import { useCiHealth } from './useCiHealth';
import CiRefreshButton from './CiRefreshButton';

const laneKey = (repository: string, lane: string) => `${repository}\n${lane}`;

// Per-run pass/fail blocks, oldest -> newest (left to right), each linking to its run.
function RunBlocks({ sequence }: { sequence: readonly RunRef[] }) {
  if (sequence.length === 0) {
    return <span className="ci-muted">no recent runs</span>;
  }
  return (
    <span className="ci-runs">
      {[...sequence].slice(0, 24).reverse().map((run) => (
        <a
          key={run.runId}
          className={run.pass ? 'ci-run ci-run-pass' : 'ci-run ci-run-fail'}
          href={run.url}
          target="_blank"
          rel="noreferrer"
          title={`run ${run.runId} — ${run.pass ? 'passed' : 'failed'}`}
        />
      ))}
    </span>
  );
}

function PatternPill({ sequence }: { sequence: readonly RunRef[] }) {
  const badge = describePattern(classifyPattern(sequence));
  return <span className={`ci-pat ${badge.tone}`}>{badge.label}</span>;
}

// Per-day pass-rate blocks for the 7d trend: green when the day was all-green, red when all-red, amber
// in between, and a faint placeholder for days with no decided runs.
function DailyBlocks({ rates }: { rates: number[] }) {
  return (
    <span className="ci-runs">
      {rates.map((rate, day) => {
        const cls = rate < 0 ? 'ci-day-empty' : rate >= 0.999 ? 'ci-run-pass' : rate <= 0.001 ? 'ci-run-fail' : 'ci-day-mixed';
        return <span key={day} className={`ci-run ${cls}`} title={rate < 0 ? 'no runs' : `${Math.round(rate * 100)}% pass`} />;
      })}
    </span>
  );
}

function cadenceLabel(minutes: number): string {
  if (!minutes) {
    return '';
  }
  return minutes % 60 === 0 ? `${minutes / 60}h` : `${minutes}m`;
}

// Whether a failing lane is something an agent can take the next step on, or needs a human.
// Derived from the triage verdict: code/test failures (real-failure, flaky) are agent-fixable;
// infra/runner and external-dependency breaks usually need a human. Returns null when there is
// no actionable verdict yet (nothing decided, or "no action"), so the row shows no actor glyph.
function ciActor(verdict: CiTriageItem | undefined): 'agent' | 'human' | null {
  if (!verdict || !verdict.needsAction) {
    return null;
  }
  return verdict.category === 'real-failure' || verdict.category === 'flaky' ? 'agent' : 'human';
}

function ActorGlyph({ actor }: { actor: 'agent' | 'human' | null }) {
  if (!actor) {
    return <span className="ci-actor ci-muted" title="no owner decided yet">·</span>;
  }
  return (
    <span className="ci-actor" title={actor === 'agent' ? 'agent-actionable' : 'needs a human'}>
      {actor === 'agent' ? '🤖' : '🧑'}
    </span>
  );
}

// Verdict cell for one failing lane: the model's actionable/not call + reason, recurrence, and suggested
// action. "Analysing…" until a verdict for this exact run exists (auto-triage fills it in on cadence).
function VerdictCell({ verdict }: { verdict: CiTriageItem | undefined }) {
  if (!verdict) {
    return <span className="ci-muted">⏳ analysing…</span>;
  }
  const recurring = verdict.recurringBuilds > 1;
  return (
    <div>
      <div>
        <span className={`bots-reason ${verdict.needsAction ? 'danger' : 'success'}`}>
          {verdict.needsAction ? '⚠️ action' : '✅ no action'}
        </span>{' '}
        <span className="ci-muted">{verdict.category} · {verdict.confidence}</span>
      </div>
      <div>{verdict.summary}</div>
      {recurring ? (
        <div className="ci-muted" title="consecutive builds failing the same way">
          ↻ same failure ×{verdict.recurringBuilds}
        </div>
      ) : null}
      {verdict.needsAction && verdict.suggestedAction && verdict.suggestedAction.toLowerCase() !== 'none' ? (
        <div className="ci-muted">→ {verdict.suggestedAction}</div>
      ) : null}
    </div>
  );
}

type FailingRow = {
  failing: FailingWorkflow;
  pulse: WorkflowPulse | undefined;
  sequence: readonly RunRef[];
  top: boolean;
  tone: 'top' | 'danger' | 'warning';
};

// One failing lane rendered as a main row + a per-run blocks sub-row. Shared by both the
// "main builds" and "other broken" cards so they stay visually identical.
function FailingLaneRows({
  row,
  verdict,
  repoLabel,
}: {
  row: FailingRow;
  verdict: CiTriageItem | undefined;
  repoLabel: (repo: string) => string;
}) {
  const { failing: f, pulse, sequence, top, tone } = row;
  const descriptor = top
    ? (pulse?.section === 'main' ? 'push · main' : 'always-show')
    : (pulse?.alwaysShow ? 'always-show' : pulse?.section === 'scheduled' ? 'scheduled' : null);
  return (
    <>
      <tr className={`ci-fail-row ${tone}`}>
        <td><ActorGlyph actor={ciActor(verdict)} /></td>
        <td>{top ? <span className="ci-pill-top">TOP</span> : <span title="red at tip">🔴</span>}</td>
        <td>
          <a href={f.lastRunUrl} target="_blank" rel="noreferrer">{f.lane} ↗</a>
          {descriptor ? <> <span className="ci-muted">{descriptor}</span></> : null}
        </td>
        <td>{repoLabel(f.repository)}</td>
        <td className="ci-muted">
          {f.streak} build{f.streak === 1 ? '' : 's'} · {formatAge(f.failingSince)}
          {f.cadenceMinutes ? <> · ~{cadenceLabel(f.cadenceMinutes)}</> : null}
        </td>
        <td><VerdictCell verdict={verdict} /></td>
      </tr>
      <tr className={`ci-subrow ${tone}`}>
        <td></td>
        <td></td>
        <td colSpan={4}>
          <div className="ci-subrow-inner">
            <span className="ci-subrow-lbl">Recent runs</span>
            <RunBlocks sequence={sequence} />
            {sequence.length > 0 ? <PatternPill sequence={sequence} /> : null}
          </div>
        </td>
      </tr>
    </>
  );
}

function FailingTable({
  rows,
  verdictByRun,
  repoLabel,
}: {
  rows: FailingRow[];
  verdictByRun: Map<number, CiTriageItem>;
  repoLabel: (repo: string) => string;
}) {
  return (
    <table className="ci-table ci-fail-table">
      <thead><tr><th></th><th></th><th>Lane</th><th>Repo</th><th>Failing</th><th>Assessment</th></tr></thead>
      <tbody>
        {rows.map((row) => (
          <FailingLaneRows
            key={`${row.failing.repository}/${row.failing.lane}`}
            row={row}
            verdict={verdictByRun.get(row.failing.lastRunId)}
            repoLabel={repoLabel}
          />
        ))}
      </tbody>
    </table>
  );
}

// The headline card: watched main builds (push on the default branch + release/* branches) that
// are red right now. These gate the branches the team ships from, so they sit at the top and are
// always shown — including the reassuring empty state when main is green.
function MainBuildsBlock({
  rows,
  verdictByRun,
  triaging,
  triageError,
  triageUpdatedAt,
  triageSnapshotError,
  onRun,
  repoLabel,
}: {
  rows: FailingRow[];
  verdictByRun: Map<number, CiTriageItem>;
  triaging: boolean;
  triageError: string | null;
  triageUpdatedAt: string | null;
  triageSnapshotError: string | null;
  onRun: () => void;
  repoLabel: (repo: string) => string;
}) {
  return (
    <section className="ci-block ci-block-main">
      <h3>
        🔥 Main builds on fire <span className="ci-muted">({rows.length})</span>
        <span className="ci-muted ci-block-sub"> · gate main &amp; release/* branches</span>
        <button type="button" className="ci-refresh" onClick={onRun} disabled={triaging}>
          {triaging ? 'Triaging…' : '🤖 Run triage'}
        </button>
      </h3>
      {triageError ? <p className="ci-refresh-error">{triageError}</p> : null}
      {triageSnapshotError ? <p className="ci-muted">Triage error: {triageSnapshotError}</p> : null}
      {rows.length === 0 ? (
        <p className="ci-empty">All watched main builds are green at tip. 🎉</p>
      ) : (
        <>
          <FailingTable rows={rows} verdictByRun={verdictByRun} repoLabel={repoLabel} />
          {triageUpdatedAt ? <p className="ci-strip-meta">triage {relativeTime(triageUpdatedAt)}</p> : null}
        </>
      )}
    </section>
  );
}

// The second card: every other lane red at tip (scheduled jobs, curated always-show runs, etc.).
// Broken, worth knowing, but not gating a ship branch — so it sits below the main card and is
// hidden entirely when nothing else is red.
function OtherBrokenBlock({
  rows,
  verdictByRun,
  repoLabel,
}: {
  rows: FailingRow[];
  verdictByRun: Map<number, CiTriageItem>;
  repoLabel: (repo: string) => string;
}) {
  if (rows.length === 0) {
    return null;
  }
  return (
    <section className="ci-block">
      <h3>
        🟠 Other broken lanes <span className="ci-muted">({rows.length})</span>
        <span className="ci-muted ci-block-sub"> · scheduled &amp; non-gating workflows</span>
      </h3>
      <FailingTable rows={rows} verdictByRun={verdictByRun} repoLabel={repoLabel} />
    </section>
  );
}

// Merged, de-emphasized "trends & healthy lanes": the old 36h/7d × main/scheduled tables collapse into
// one details block. Each lane shows its per-run blocks + pattern (richer of the 36h pulse sequence and
// the wider weekly history, so sparse lanes still classify) plus the 7d daily trend, degrading first.
function TrendsDetails({ weekly, pulseByLane, greenCount, repoLabel }: { weekly: WorkflowWeekly[]; pulseByLane: Map<string, WorkflowPulse>; greenCount: number; repoLabel: (repo: string) => string }) {
  const sorted = [...weekly].sort((a, b) => a.delta - b.delta);
  const avgPass = weekly.length ? weekly.reduce((sum, w) => sum + w.passRate, 0) / weekly.length : 1;
  return (
    <details className="ci-trends">
      <summary>
        🩺 Trends &amp; healthy lanes <span className="ci-muted">— {greenCount} green at tip · 7d pass {percent(avgPass)}</span>
      </summary>
      {sorted.length === 0 ? (
        <p className="ci-empty">No trend data yet.</p>
      ) : (
        <table className="ci-table">
          <thead><tr><th>Repo</th><th>Lane</th><th>Recent</th><th>7d daily</th><th>7d pass</th><th>vs prior</th></tr></thead>
          <tbody>
            {sorted.map((w) => {
              const sequence = pickRunSequence(pulseByLane.get(laneKey(w.repository, w.lane))?.sequence ?? [], w.recentRuns);
              return (
                <tr key={`${w.repository}/${w.lane}`} className={w.delta < 0 ? 'ci-degrading' : undefined}>
                  <td>{repoLabel(w.repository)}</td>
                  <td>{w.lane}</td>
                  <td>
                    <div className="ci-recent-cell">
                      <RunBlocks sequence={sequence} />
                      {sequence.length > 0 ? <PatternPill sequence={sequence} /> : null}
                    </div>
                  </td>
                  <td><DailyBlocks rates={w.dailyPassRates} /></td>
                  <td>{percent(w.passRate)}</td>
                  <td>{delta(w.delta)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </details>
  );
}

function CiHealthView() {
  const { data, error, refreshing, refreshError, refresh, triaging, triageError, triage } = useCiHealth();

  if (error) {
    return <div className="ci-health-empty">Could not load CI health: {error}</div>;
  }

  if (!data) {
    return <div className="ci-health-empty">Loading CI health…</div>;
  }

  const { pulse, weekly } = data;
  const workflows = pulse?.workflows ?? [];
  const pulseByLane = new Map(workflows.map((w) => [laneKey(w.repository, w.lane), w]));
  const weeklyWorkflows = weekly?.workflows ?? [];
  const weeklyByLane = new Map(weeklyWorkflows.map((w) => [laneKey(w.repository, w.lane), w]));
  const repoLabel = buildRepoLabeler([
    ...workflows.map((w) => w.repository),
    ...weeklyWorkflows.map((w) => w.repository),
  ]);

  // Build the failing rows, then split into the watched main builds (push on the default branch
  // and release/* branches — section "main") and everything else red. Within each card, rank by
  // likely-real then longest streak. Each row's blocks/pattern use the richer of the 36h pulse
  // sequence and the wider weekly history.
  const allFailing: FailingRow[] = (pulse?.failingNow ?? [])
    .map((failing) => {
      const lanePulse = pulseByLane.get(laneKey(failing.repository, failing.lane));
      const weeklyLane = weeklyByLane.get(laneKey(failing.repository, failing.lane));
      const isMain = failing.section === 'main';
      const tone: FailingRow['tone'] = isMain ? 'top' : failing.likelyReal ? 'danger' : 'warning';
      return { failing, pulse: lanePulse, sequence: pickRunSequence(lanePulse?.sequence ?? [], weeklyLane?.recentRuns), top: isMain, tone };
    });
  const rankFailing = (a: FailingRow, b: FailingRow) =>
    Number(b.failing.likelyReal) - Number(a.failing.likelyReal) || b.failing.streak - a.failing.streak;
  const mainRows = allFailing.filter((r) => r.top).sort(rankFailing);
  const otherRows = allFailing.filter((r) => !r.top).sort(rankFailing);
  const verdictByRun = new Map((data.triage?.items ?? []).map((it) => [it.runId, it]));

  const greenCount = workflows.filter((w) => w.greenAtTip).length;
  const redCount = allFailing.length;

  return (
    <div className="ci-health">
      <section className="ci-strip">
        <strong>{redCount === 0 ? '🟢 CI: all lanes green at tip' : `🟡 CI: ${redCount} lane${redCount === 1 ? '' : 's'} red at tip`}</strong>
        <span className="ci-strip-meta">
          {redCount > 0 ? <>{mainRows.length} main · {otherRows.length} other · </> : null}
          {pulse ? `pulse ${relativeTime(pulse.updatedAt)}` : 'pulse pending'} ·{' '}
          {weekly ? `weekly ${relativeTime(weekly.updatedAt)}` : 'weekly pending'}
        </span>
        <CiRefreshButton refreshing={refreshing} refreshError={refreshError} onRefresh={refresh} />
      </section>

      <MainBuildsBlock
        rows={mainRows}
        verdictByRun={verdictByRun}
        triaging={triaging}
        triageError={triageError}
        triageUpdatedAt={data.triage?.updatedAt ?? null}
        triageSnapshotError={data.triage?.error ?? null}
        onRun={triage}
        repoLabel={repoLabel}
      />

      <OtherBrokenBlock rows={otherRows} verdictByRun={verdictByRun} repoLabel={repoLabel} />

      <TrendsDetails weekly={weeklyWorkflows} pulseByLane={pulseByLane} greenCount={greenCount} repoLabel={repoLabel} />
    </div>
  );
}

export default CiHealthView;

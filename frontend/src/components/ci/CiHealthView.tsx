import { useState } from 'react';
import type { CiTriageItem, CiTriageSnapshot, FailingWorkflow, WorkflowPulse, WorkflowWeekly } from '../../types';
import { formatAge } from '../../utils/format';
import { buildRepoLabeler, delta, percent, relativeTime } from './ciFormat';
import { useCiHealth } from './useCiHealth';
import CiRefreshButton from './CiRefreshButton';

// Newest-first sequence rendered oldest -> newest (left to right), each block linking to its run.
function RecentRuns({ lane }: { lane: WorkflowPulse }) {
  return (
    <span className="ci-runs">
      {[...lane.sequence].slice(0, 30).reverse().map((run) => (
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

function PulseTable({ lanes, weeklyByLane, repoLabel }: { lanes: WorkflowPulse[]; weeklyByLane: Map<string, WorkflowWeekly>; repoLabel: (repo: string) => string }) {
  if (lanes.length === 0) {
    return <p className="ci-empty">No lanes.</p>;
  }

  return (
    <table className="ci-table">
      <thead><tr><th>Tip</th><th>Repo</th><th>Lane</th><th>Recent</th><th>Pass</th><th>Δ7d</th></tr></thead>
      <tbody>
        {lanes.map((w) => {
          const wk = weeklyByLane.get(`${w.repository}\n${w.lane}`);
          const d = wk ? w.passRate - wk.passRate : null;
          return (
            <tr key={`${w.repository}/${w.lane}`}>
              <td title={w.greenAtTip ? 'green at tip' : 'red at tip'}>{w.greenAtTip ? '🟢' : '🔴'}</td>
              <td>{repoLabel(w.repository)}</td>
              <td>{w.lane}</td>
              <td><RecentRuns lane={w} /></td>
              <td>{percent(w.passRate)} <span className="ci-muted">({w.passes}/{w.runs})</span></td>
              <td>{d === null ? '—' : delta(d)}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
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

function WeeklyTable({ lanes, repoLabel }: { lanes: WorkflowWeekly[]; repoLabel: (repo: string) => string }) {
  if (lanes.length === 0) {
    return <p className="ci-empty">No lanes.</p>;
  }

  return (
    <table className="ci-table">
      <thead><tr><th>Repo</th><th>Lane</th><th>Daily</th><th>7d pass</th><th>vs prior</th></tr></thead>
      <tbody>
        {lanes.map((w) => (
          <tr key={`${w.repository}/${w.lane}`}>
            <td>{repoLabel(w.repository)}</td>
            <td>{w.lane}</td>
            <td><DailyBlocks rates={w.dailyPassRates} /></td>
            <td>{percent(w.passRate)}</td>
            <td>{delta(w.delta)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function cadenceLabel(minutes: number): string {
  if (!minutes) {
    return '';
  }
  return minutes % 60 === 0 ? `${minutes / 60}h` : `${minutes}m`;
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

// "Failing now" — every lane whose latest run failed (red at tip), each with its triage verdict inline.
// The list is always complete; verdicts fill in from auto-triage (or the manual "Run triage").
function FailingNowBlock({
  failing,
  triage,
  triaging,
  triageError,
  onRun,
  repoLabel,
}: {
  failing: FailingWorkflow[];
  triage: CiTriageSnapshot | null;
  triaging: boolean;
  triageError: string | null;
  onRun: () => void;
  repoLabel: (repo: string) => string;
}) {
  const verdictByRun = new Map((triage?.items ?? []).map((it) => [it.runId, it]));

  return (
    <section className="ci-block">
      <h3>
        🚨 Failing now <span className="ci-muted">({failing.length})</span>
        <button type="button" className="ci-refresh" onClick={onRun} disabled={triaging}>
          {triaging ? 'Triaging…' : '🤖 Run triage'}
        </button>
      </h3>
      {triageError ? <p className="ci-refresh-error">{triageError}</p> : null}
      {triage?.error ? <p className="ci-muted">Triage error: {triage.error}</p> : null}
      {failing.length === 0 ? (
        <p className="ci-empty">No lanes are red at tip. 🎉</p>
      ) : (
        <>
          <table className="ci-table">
            <thead><tr><th>Repo</th><th>Lane</th><th>Failing</th><th>Assessment</th></tr></thead>
            <tbody>
              {failing.map((f) => (
                <tr key={`${f.repository}/${f.lane}`}>
                  <td>{repoLabel(f.repository)}</td>
                  <td>
                    <a href={f.lastRunUrl} target="_blank" rel="noreferrer">{f.lane} ↗</a>
                  </td>
                  <td className="ci-muted">
                    {f.streak} build{f.streak === 1 ? '' : 's'} · {formatAge(f.failingSince)}
                    {f.cadenceMinutes ? <> · ~{cadenceLabel(f.cadenceMinutes)}</> : null}
                  </td>
                  <td><VerdictCell verdict={verdictByRun.get(f.lastRunId)} /></td>
                </tr>
              ))}
            </tbody>
          </table>
          {triage ? <p className="ci-strip-meta">triage {relativeTime(triage.updatedAt)}</p> : null}
        </>
      )}
    </section>
  );
}

function CiHealthView() {
  const { data, error, refreshing, refreshError, refresh, triaging, triageError, triage } = useCiHealth();
  const [showAllScheduled, setShowAllScheduled] = useState(false);

  if (error) {
    return <div className="ci-health-empty">Could not load CI health: {error}</div>;
  }

  if (!data) {
    return <div className="ci-health-empty">Loading CI health…</div>;
  }

  const { pulse, weekly } = data;
  const redAtTip = (pulse?.workflows ?? []).filter((w) => !w.greenAtTip).length;
  const weeklyByLane = new Map((weekly?.workflows ?? []).map((w) => [`${w.repository}\n${w.lane}`, w]));
  const repoLabel = buildRepoLabeler([
    ...(pulse?.workflows ?? []).map((w) => w.repository),
    ...(weekly?.workflows ?? []).map((w) => w.repository),
  ]);

  const pulseMain = (pulse?.workflows ?? []).filter((w) => w.section === 'main');
  const allScheduled = (pulse?.workflows ?? []).filter((w) => w.section === 'scheduled');
  // Always-show lanes first, then those currently failing; the rest hide behind the toggle.
  const scheduledShown = showAllScheduled
    ? [...allScheduled].sort((a, b) => Number(b.alwaysShow) - Number(a.alwaysShow))
    : allScheduled
        .filter((w) => w.alwaysShow || !w.greenAtTip)
        .sort((a, b) => Number(b.alwaysShow) - Number(a.alwaysShow));
  const scheduledHidden = allScheduled.length - scheduledShown.length;

  const weeklyMain = (weekly?.workflows ?? []).filter((w) => w.section === 'main');
  const weeklyScheduled = (weekly?.workflows ?? []).filter(
    (w) => w.section === 'scheduled' && (showAllScheduled || w.alwaysShow || w.passRate < 1),
  ).sort((a, b) => Number(b.alwaysShow) - Number(a.alwaysShow));

  return (
    <div className="ci-health">
      <section className="ci-strip">
        <strong>{redAtTip === 0 ? '🟢 CI: all lanes green at tip' : `🟡 CI: ${redAtTip} lane(s) red at tip`}</strong>
        <span className="ci-strip-meta">
          {pulse ? `pulse ${relativeTime(pulse.updatedAt)}` : 'pulse pending'} ·{' '}
          {weekly ? `weekly ${relativeTime(weekly.updatedAt)}` : 'weekly pending'}
        </span>
        <CiRefreshButton refreshing={refreshing} refreshError={refreshError} onRefresh={refresh} />
      </section>

      <FailingNowBlock
        failing={pulse?.failingNow ?? []}
        triage={data.triage}
        triaging={triaging}
        triageError={triageError}
        onRun={triage}
        repoLabel={repoLabel}
      />

      <section className="ci-block">
        <h3>🌳 Main CI — 36h pass rate</h3>
        <PulseTable lanes={pulseMain} weeklyByLane={weeklyByLane} repoLabel={repoLabel} />
      </section>

      <section className="ci-block">
        <h3>
          🗓️ Scheduled — 36h pass rate
          {scheduledHidden > 0 || showAllScheduled ? (
            <button type="button" className="ci-toggle" onClick={() => setShowAllScheduled((v) => !v)}>
              {showAllScheduled ? 'show only failing' : `show all (+${scheduledHidden} passing)`}
            </button>
          ) : null}
        </h3>
        <PulseTable lanes={scheduledShown} weeklyByLane={weeklyByLane} repoLabel={repoLabel} />
      </section>

      <section className="ci-block">
        <h3>🩺 Main CI — 7d trend</h3>
        <WeeklyTable lanes={weeklyMain} repoLabel={repoLabel} />
      </section>

      <section className="ci-block">
        <h3>🩺 Scheduled — 7d trend</h3>
        <WeeklyTable lanes={weeklyScheduled} repoLabel={repoLabel} />
      </section>
    </div>
  );
}

export default CiHealthView;

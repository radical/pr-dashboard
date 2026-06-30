import { useEffect, useState } from 'react';
import type { CiHealthResponse, WorkflowPulse, WorkflowWeekly } from '../../types';
import { fetchCiHealth } from '../../utils/ciHealth';
import { buildRepoLabeler, delta, percent, relativeTime } from './ciFormat';

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
      <thead><tr><th>Tip</th><th>Repo / lane</th><th>Pass</th><th>Δ7d</th><th>Recent</th></tr></thead>
      <tbody>
        {lanes.map((w) => {
          const wk = weeklyByLane.get(`${w.repository}\n${w.lane}`);
          const d = wk ? w.passRate - wk.passRate : null;
          return (
            <tr key={`${w.repository}/${w.lane}`}>
              <td title={w.greenAtTip ? 'green at tip' : 'red at tip'}>{w.greenAtTip ? '🟢' : '🔴'}</td>
              <td>{repoLabel(w.repository)} · {w.lane}</td>
              <td>{percent(w.passRate)} <span className="ci-muted">({w.passes}/{w.runs})</span></td>
              <td>{d === null ? '—' : delta(d)}</td>
              <td><RecentRuns lane={w} /></td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function WeeklyTable({ lanes, repoLabel }: { lanes: WorkflowWeekly[]; repoLabel: (repo: string) => string }) {
  if (lanes.length === 0) {
    return <p className="ci-empty">No lanes.</p>;
  }

  return (
    <table className="ci-table">
      <thead><tr><th>Repo / lane</th><th>7d pass</th><th>vs prior</th></tr></thead>
      <tbody>
        {lanes.map((w) => (
          <tr key={`${w.repository}/${w.lane}`}>
            <td>{repoLabel(w.repository)} · {w.lane}</td>
            <td>{percent(w.passRate)}</td>
            <td>{delta(w.delta)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function CiHealthView() {
  const [data, setData] = useState<CiHealthResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showAllScheduled, setShowAllScheduled] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    fetchCiHealth(controller.signal)
      .then(setData)
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : 'Failed to load CI health');
        }
      });
    return () => controller.abort();
  }, []);

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
      </section>

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

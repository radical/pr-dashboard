import { useState } from 'react';
import type { CiTriageSnapshot, WorkflowPulse, WorkflowWeekly } from '../../types';
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

// Copilot-produced triage of the currently-failing lanes. "Run triage" shells out to the Copilot CLI on
// the server (a few minutes) and swaps in the result; each item links to its failing run.
function TriageBlock({
  triage,
  triaging,
  triageError,
  onRun,
  repoLabel,
}: {
  triage: CiTriageSnapshot | null;
  triaging: boolean;
  triageError: string | null;
  onRun: () => void;
  repoLabel: (repo: string) => string;
}) {
  return (
    <section className="ci-block">
      <h3>
        🔎 CI failure triage (Copilot)
        <button type="button" className="ci-refresh" onClick={onRun} disabled={triaging}>
          {triaging ? 'Running triage…' : '🤖 Run triage'}
        </button>
      </h3>
      {triageError ? <p className="ci-refresh-error">{triageError}</p> : null}
      {triaging ? (
        <p className="ci-muted">Asking Copilot to investigate the failing lanes — this can take a few minutes…</p>
      ) : null}
      {!triage ? (
        <p className="ci-empty">No triage yet. Click “Run triage” to ask Copilot to investigate the currently-failing lanes.</p>
      ) : triage.error ? (
        <p className="ci-empty">Triage error: {triage.error}</p>
      ) : triage.items.length === 0 ? (
        <p className="ci-empty">No failing lanes to triage. 🎉</p>
      ) : (
        <>
          <table className="ci-table">
            <thead><tr><th>Action</th><th>Repo / workflow</th><th>Category</th><th>Confidence</th><th>Assessment</th></tr></thead>
            <tbody>
              {triage.items.map((it) => (
                <tr key={`${it.repository}#${it.runId}`}>
                  <td title={it.needsAction ? 'needs action' : 'no action needed'}>{it.needsAction ? '⚠️' : '✅'}</td>
                  <td><a href={it.runUrl} target="_blank" rel="noreferrer">{repoLabel(it.repository)} · {it.workflow} ↗</a></td>
                  <td>{it.category}</td>
                  <td>{it.confidence}</td>
                  <td>
                    <div>{it.summary}</div>
                    {it.needsAction && it.suggestedAction && it.suggestedAction.toLowerCase() !== 'none' ? (
                      <div className="ci-muted">→ {it.suggestedAction}</div>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="ci-strip-meta">triage {relativeTime(triage.updatedAt)}</p>
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

      <TriageBlock
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

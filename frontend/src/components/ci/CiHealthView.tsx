import { useEffect, useState } from 'react';
import type { CiHealthResponse, WorkflowPulse, WorkflowWeekly } from '../../types';
import { fetchCiHealth } from '../../utils/ciHealth';

function percent(value: number): string {
  return `${Math.round(value * 100)}%`;
}

function delta(value: number): string {
  return value >= 0 ? `▲ +${percent(value)}` : `▼ ${percent(Math.abs(value))}`;
}

function relativeTime(iso: string): string {
  const minutes = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

function PulseTable({ lanes, weeklyByLane }: { lanes: WorkflowPulse[]; weeklyByLane: Map<string, WorkflowWeekly> }) {
  if (lanes.length === 0) {
    return <p className="ci-empty">No lanes.</p>;
  }

  return (
    <table className="ci-table">
      <thead><tr><th>Tip</th><th>Repo / lane</th><th>Runs</th><th>Pass</th><th>Δ7d</th><th>Recent</th></tr></thead>
      <tbody>
        {lanes.map((w) => {
          const wk = weeklyByLane.get(`${w.repository}\n${w.lane}`);
          const d = wk ? w.passRate - wk.passRate : null;
          return (
            <tr key={`${w.repository}/${w.lane}`}>
              <td title={w.greenAtTip ? 'green at tip' : 'red at tip'}>{w.greenAtTip ? '🟢' : '🔴'}</td>
              <td>{w.repository} · {w.lane}</td>
              <td>{w.runs}</td>
              <td>{percent(w.passRate)}</td>
              <td>{d === null ? '—' : delta(d)}</td>
              <td>{[...w.sequence].slice(0, 30).reverse().map((pass) => (pass ? '🟩' : '🟥')).join('')}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function WeeklyTable({ lanes }: { lanes: WorkflowWeekly[] }) {
  if (lanes.length === 0) {
    return <p className="ci-empty">No lanes.</p>;
  }

  return (
    <table className="ci-table">
      <thead><tr><th>Repo / lane</th><th>7d pass</th><th>vs prior</th></tr></thead>
      <tbody>
        {lanes.map((w) => (
          <tr key={`${w.repository}/${w.lane}`}>
            <td>{w.repository} · {w.lane}</td>
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
  const redAtTip = pulse?.failingNow.length ?? 0;
  const weeklyByLane = new Map((weekly?.workflows ?? []).map((w) => [`${w.repository}\n${w.lane}`, w]));
  const pulseMain = (pulse?.workflows ?? []).filter((w) => w.section === 'main');
  const pulseScheduled = (pulse?.workflows ?? []).filter((w) => w.section === 'scheduled');
  const weeklyMain = (weekly?.workflows ?? []).filter((w) => w.section === 'main');
  const weeklyScheduled = (weekly?.workflows ?? []).filter((w) => w.section === 'scheduled');

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
        <h3>⚠️ Failing lanes now</h3>
        {redAtTip === 0 ? (
          <p className="ci-empty">Nothing on fire.</p>
        ) : (
          <table className="ci-table">
            <thead><tr><th>Repo</th><th>Lane</th><th>Streak</th><th>Signal</th><th>Run</th><th /></tr></thead>
            <tbody>
              {pulse!.failingNow.map((f) => (
                <tr key={`${f.repository}/${f.lane}`}>
                  <td>{f.repository}</td>
                  <td>{f.lane}</td>
                  <td>{f.streak}</td>
                  <td>{f.likelyReal ? 'likely real' : 'maybe flaky'}</td>
                  <td><a href={f.lastRunUrl} target="_blank" rel="noreferrer">run ↗</a></td>
                  <td><span className="ci-ghost-action" title="Coming later">Launch fix · later</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section className="ci-block">
        <h3>🌳 Main CI — 36h pass rate</h3>
        <PulseTable lanes={pulseMain} weeklyByLane={weeklyByLane} />
      </section>

      <section className="ci-block">
        <h3>🗓️ Scheduled — 36h pass rate</h3>
        <PulseTable lanes={pulseScheduled} weeklyByLane={weeklyByLane} />
      </section>

      <section className="ci-block">
        <h3>🩺 Main CI — 7d trend</h3>
        <WeeklyTable lanes={weeklyMain} />
      </section>

      <section className="ci-block">
        <h3>🩺 Scheduled — 7d trend</h3>
        <WeeklyTable lanes={weeklyScheduled} />
      </section>

      <section className="ci-block">
        <h3>🤖 Bot / automated PRs</h3>
        {(pulse?.botPrs.length ?? 0) === 0 ? (
          <p className="ci-empty">No open bot/automated PRs.</p>
        ) : (
          <table className="ci-table">
            <thead><tr><th>PR</th><th>Title</th><th>Repo</th><th>Author</th><th>CI</th><th>Mergeable</th><th>Review</th></tr></thead>
            <tbody>
              {pulse!.botPrs.map((pr) => (
                <tr key={`${pr.repository}#${pr.number}`}>
                  <td><a href={pr.htmlUrl} target="_blank" rel="noreferrer">#{pr.number} ↗</a></td>
                  <td><a href={pr.htmlUrl} target="_blank" rel="noreferrer">{pr.title}</a></td>
                  <td>{pr.repository}</td>
                  <td>{pr.author}</td>
                  <td>{pr.ciStatus}</td>
                  <td>{pr.mergeable}</td>
                  <td>{pr.review.replace('_', ' ')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section className="ci-block">
        <h3>📨 Bot / automated issues</h3>
        {(pulse?.botIssues.length ?? 0) === 0 ? (
          <p className="ci-empty">No open bot/automated issues.</p>
        ) : (
          <table className="ci-table">
            <thead><tr><th>Issue</th><th>Title</th><th>Repo</th><th>Author</th></tr></thead>
            <tbody>
              {pulse!.botIssues.map((issue) => (
                <tr key={`${issue.repository}#${issue.number}`}>
                  <td><a href={issue.htmlUrl} target="_blank" rel="noreferrer">#{issue.number} ↗</a></td>
                  <td><a href={issue.htmlUrl} target="_blank" rel="noreferrer">{issue.title}</a></td>
                  <td>{issue.repository}</td>
                  <td>{issue.author}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}

export default CiHealthView;

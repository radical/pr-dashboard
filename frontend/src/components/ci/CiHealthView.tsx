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

// Drops the owner prefix ("microsoft/aspire" -> "aspire") for readability, but only when the bare repo
// name is unique among the repos on screen — so e.g. microsoft/aspire vs CommunityToolkit/Aspire keep
// their owners and don't collide.
function buildRepoLabeler(repos: Iterable<string>): (repo: string) => string {
  const ownersByName = new Map<string, Set<string>>();
  for (const repo of repos) {
    const slash = repo.indexOf('/');
    if (slash < 0) continue;
    const name = repo.slice(slash + 1).toLowerCase();
    const owner = repo.slice(0, slash);
    let owners = ownersByName.get(name);
    if (!owners) {
      owners = new Set();
      ownersByName.set(name, owners);
    }
    owners.add(owner);
  }

  return (repo: string) => {
    const slash = repo.indexOf('/');
    if (slash < 0) return repo;
    const name = repo.slice(slash + 1);
    return (ownersByName.get(name.toLowerCase())?.size ?? 0) > 1 ? repo : name;
  };
}

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
    ...(pulse?.botPrs ?? []).map((b) => b.repository),
    ...(pulse?.botIssues ?? []).map((b) => b.repository),
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
                  <td>{repoLabel(pr.repository)}</td>
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
                  <td>{repoLabel(issue.repository)}</td>
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

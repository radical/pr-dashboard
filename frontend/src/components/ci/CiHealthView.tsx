import { useEffect, useState } from 'react';
import type { CiHealthResponse } from '../../types';
import { fetchCiHealth } from '../../utils/ciHealth';

function percent(value: number): string {
  return `${Math.round(value * 100)}%`;
}

function relativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  const minutes = Math.max(0, Math.round((Date.now() - then) / 60000));
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
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
  // Join a pulse lane to its weekly lane (by repo + lane) to show 36h-vs-7d drift.
  const weeklyByLane = new Map((weekly?.workflows ?? []).map((w) => [`${w.repository}\n${w.lane}`, w]));

  return (
    <div className="ci-health">
      {/* 1. Status strip */}
      <section className="ci-strip">
        <strong>{redAtTip === 0 ? '🟢 CI: all lanes green at tip' : `🟡 CI: ${redAtTip} lane(s) red at tip`}</strong>
        <span className="ci-strip-meta">
          {pulse ? `pulse ${relativeTime(pulse.updatedAt)}` : 'pulse pending'} ·{' '}
          {weekly ? `weekly ${relativeTime(weekly.updatedAt)}` : 'weekly pending'}
        </span>
      </section>

      {/* 2. Failing workflows now */}
      <section className="ci-block">
        <h3>⚠️ Failing lanes now</h3>
        {redAtTip === 0 ? (
          <p className="ci-empty">Nothing on fire.</p>
        ) : (
          <table className="ci-table">
            <thead>
              <tr><th>Repo</th><th>Lane</th><th>Streak</th><th>Signal</th><th>Run</th><th /></tr>
            </thead>
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

      {/* 3. Daily pulse (36h) */}
      <section className="ci-block">
        <h3>📈 Daily pulse — 36h pass rate</h3>
        <table className="ci-table">
          <thead><tr><th>Tip</th><th>Repo / lane</th><th>Runs</th><th>Pass</th><th>Δ7d</th><th>Recent</th></tr></thead>
          <tbody>
            {(pulse?.workflows ?? []).map((w) => {
              const wk = weeklyByLane.get(`${w.repository}\n${w.lane}`);
              const delta = wk ? w.passRate - wk.passRate : null;
              return (
                <tr key={`${w.repository}/${w.lane}`}>
                  <td title={w.greenAtTip ? 'green at tip' : 'red at tip'}>{w.greenAtTip ? '🟢' : '🔴'}</td>
                  <td>{w.repository} · {w.lane}</td>
                  <td>{w.runs}</td>
                  <td>{percent(w.passRate)}</td>
                  <td>{delta === null ? '—' : delta >= 0 ? `▲ +${percent(delta)}` : `▼ ${percent(Math.abs(delta))}`}</td>
                  <td>{[...w.sequence].slice(0, 30).reverse().map((pass) => (pass ? '🟩' : '🟥')).join('')}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </section>

      {/* 4. Weekly health (7d) */}
      <section className="ci-block">
        <h3>🩺 Weekly health — 7d trend</h3>
        <table className="ci-table">
          <thead><tr><th>Repo / lane</th><th>7d pass</th><th>vs prior</th></tr></thead>
          <tbody>
            {(weekly?.workflows ?? []).map((w) => (
              <tr key={`${w.repository}/${w.lane}`}>
                <td>{w.repository} · {w.lane}</td>
                <td>{percent(w.passRate)}</td>
                <td>{w.delta >= 0 ? `▲ +${percent(w.delta)}` : `▼ ${percent(Math.abs(w.delta))}`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {/* 5. Bot / automated PRs */}
      <section className="ci-block">
        <h3>🤖 Bot / automated PRs</h3>
        {(pulse?.botPrs.length ?? 0) === 0 ? (
          <p className="ci-empty">No open bot/automated PRs.</p>
        ) : (
          <table className="ci-table">
            <thead><tr><th>PR</th><th>Repo</th><th>Author</th><th>CI</th></tr></thead>
            <tbody>
              {pulse!.botPrs.map((pr) => (
                <tr key={`${pr.repository}#${pr.number}`}>
                  <td><a href={pr.htmlUrl} target="_blank" rel="noreferrer">#{pr.number} ↗</a></td>
                  <td>{pr.repository}</td>
                  <td>{pr.author}</td>
                  <td>{pr.ciStatus}</td>
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

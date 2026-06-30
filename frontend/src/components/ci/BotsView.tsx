import { useEffect, useState } from 'react';
import type { CiHealthResponse } from '../../types';
import { fetchCiHealth } from '../../utils/ciHealth';
import { buildRepoLabeler, relativeTime } from './ciFormat';

function BotsView() {
  const [data, setData] = useState<CiHealthResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchCiHealth(controller.signal)
      .then(setData)
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : 'Failed to load bot activity');
        }
      });
    return () => controller.abort();
  }, []);

  if (error) {
    return <div className="ci-health-empty">Could not load bot activity: {error}</div>;
  }

  if (!data) {
    return <div className="ci-health-empty">Loading bot activity…</div>;
  }

  const { pulse } = data;
  const repoLabel = buildRepoLabeler([
    ...(pulse?.botPrs ?? []).map((b) => b.repository),
    ...(pulse?.botIssues ?? []).map((b) => b.repository),
  ]);

  return (
    <div className="ci-health">
      <section className="ci-strip">
        <strong>🤖 Bot / automated activity</strong>
        <span className="ci-strip-meta">
          {pulse ? `pulse ${relativeTime(pulse.updatedAt)}` : 'pulse pending'}
        </span>
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

export default BotsView;

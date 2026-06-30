import type { BotShepherdSnapshot, WorkflowPulse } from '../../types';
import { bucketBotPrs } from '../../utils/botBuckets';
import { formatAge } from '../../utils/format';
import { buildBotAttentionRows, type BotAttentionRow, type LaneMeta } from './botAttention';
import { buildRepoLabeler, relativeTime } from './ciFormat';
import { pickRunSequence } from './ciPattern';
import { useCiHealth } from './useCiHealth';
import CiRefreshButton from './CiRefreshButton';

const laneKey = (repository: string, lane: string) => `${repository}\n${lane}`;

function shepherdMeta(shepherd: BotShepherdSnapshot | null): string {
  if (!shepherd) {
    return '';
  }
  const cached = shepherd.fromCache ? ' (cached)' : '';
  return ` · shepherd ${relativeTime(shepherd.updatedAt)}${cached}`;
}

// One row of the unified "Needs attention" table, plus a context sub-row beneath it (mirrors the CI
// page's failing-now table: tone-tinted row + spanning sub-row).
function AttentionRow({ row, repoLabel }: { row: BotAttentionRow; repoLabel: (repo: string) => string }) {
  const titleCell = row.htmlUrl ? (
    <a href={row.htmlUrl} target="_blank" rel="noreferrer">{row.title} ↗</a>
  ) : (
    row.title
  );
  return (
    <>
      <tr className={`bots-row ${row.tone}`}>
        <td className="bots-actor" title={row.actor === 'human' ? 'human-only' : 'agent-actionable'}>
          {row.actor === 'human' ? '🧑' : '🤖'}
        </td>
        <td className="bots-title">{titleCell}</td>
        <td>{repoLabel(row.repository)}</td>
        <td><span className={`bots-reason ${row.tone}`}>{row.why}</span></td>
        <td className="bots-action">{row.action ? <><span className="ci-muted">→</span> {row.action}</> : null}</td>
        <td className="ci-muted">{row.createdAt ? formatAge(row.createdAt) : '—'}</td>
      </tr>
      {row.context ? (
        <tr className={`bots-subrow ${row.tone}`}>
          <td></td>
          <td colSpan={5}><span className="ci-muted">↳ {row.context}</span></td>
        </tr>
      ) : null}
    </>
  );
}

function BotsView() {
  const { data, error, refreshing, refreshError, refresh, shepherding, shepherdError, shepherd } = useCiHealth();

  if (error) {
    return <div className="ci-health-empty">Could not load bot activity: {error}</div>;
  }

  if (!data) {
    return <div className="ci-health-empty">Loading bot activity…</div>;
  }

  const { pulse, weekly, botShepherd } = data;
  const botPrs = pulse?.botPrs ?? [];
  const botIssues = pulse?.botIssues ?? [];
  const failingNow = pulse?.failingNow ?? [];
  const notes = botShepherd?.prNotes ?? [];
  const issueGroups = botShepherd?.issueGroups ?? [];
  const workQueue = botShepherd?.workQueue ?? [];

  // Pulse-derived per-lane metadata, so the builder can mark top-tier CI breaks and label their pattern.
  // Use the richer of the 36h pulse sequence and the wider weekly history so sparse lanes still classify.
  const weeklyByLane = new Map((weekly?.workflows ?? []).map((w) => [laneKey(w.repository, w.lane), w] as const));
  const laneMeta = new Map<string, LaneMeta>(
    (pulse?.workflows ?? []).map((w: WorkflowPulse) => [
      laneKey(w.repository, w.lane),
      {
        topTier: w.section === 'main' || w.alwaysShow,
        sequence: [...pickRunSequence(w.sequence, weeklyByLane.get(laneKey(w.repository, w.lane))?.recentRuns)],
      },
    ]),
  );

  const rows = buildBotAttentionRows({ botPrs, notes, workQueue, issueGroups, botIssues, failingNow, laneMeta });

  // "Calm": ready-to-merge / pending bot PRs that don't need attention (handled on the Review page).
  const overrideBucket = notes.length > 0
    ? (pr: typeof botPrs[number]) => {
        const bucket = notes.find((n) => n.repository === pr.repository && n.number === pr.number)?.bucket;
        return bucket === 'easy-win' || bucket === 'stuck' || bucket === 'broken' || bucket === 'pending'
          ? (bucket as 'easy-win' | 'stuck' | 'broken' | 'pending')
          : undefined;
      }
    : undefined;
  const calmCount = bucketBotPrs(botPrs, overrideBucket)
    .filter((bucket) => bucket.id === 'easy-win' || bucket.id === 'pending')
    .reduce((total, bucket) => total + bucket.items.length, 0);

  const repoLabel = buildRepoLabeler([
    ...botPrs.map((b) => b.repository),
    ...botIssues.map((b) => b.repository),
    ...failingNow.map((f) => f.repository),
    ...workQueue.map((b) => b.repository),
  ]);

  return (
    <div className="ci-health">
      <section className="ci-strip">
        <strong>🤖 Bot activity — {rows.length} need{rows.length === 1 ? 's' : ''} attention</strong>
        <span className="ci-strip-meta">
          {pulse ? `pulse ${relativeTime(pulse.updatedAt)}` : 'pulse pending'}
          {shepherdMeta(botShepherd)}
        </span>
        <span className="ci-refresh-wrap">
          {shepherdError ? <span className="ci-refresh-error">{shepherdError}</span> : null}
          <button type="button" className="ci-refresh" onClick={shepherd} disabled={shepherding}>
            {shepherding ? 'Shepherding…' : '🐑 Shepherd'}
          </button>
          <CiRefreshButton refreshing={refreshing} refreshError={refreshError} onRefresh={refresh} />
        </span>
      </section>

      {shepherding ? (
        <p className="ci-muted">Asking Copilot to shepherd the open bot PRs and issues — a minute or two…</p>
      ) : null}
      {botShepherd?.error ? <p className="ci-empty">Shepherd error: {botShepherd.error}</p> : null}

      <section className="ci-block">
        <h3>⏳ Needs attention <span className="ci-muted">({rows.length})</span></h3>
        {rows.length === 0 ? (
          <p className="ci-empty">Nothing needs attention — no stuck/broken bot PRs, auto-issues, or unlinked CI breaks. 🎉</p>
        ) : (
          <table className="ci-table bots-attention-table">
            <thead><tr><th></th><th>Item</th><th>Repo</th><th>Why</th><th>Action</th><th>Age</th></tr></thead>
            <tbody>
              {rows.map((row) => (
                <AttentionRow key={row.key} row={row} repoLabel={repoLabel} />
              ))}
            </tbody>
          </table>
        )}
      </section>

      {calmCount > 0 ? (
        <details className="ci-trends">
          <summary>🟢 Calm <span className="ci-muted">— {calmCount} ready-to-merge / pending bot PR{calmCount === 1 ? '' : 's'}</span></summary>
          <p className="ci-muted">
            These are green + mergeable or still running checks — they surface on the{' '}
            <a href="?mode=review">Review page’s bots lane ↗</a>.
          </p>
        </details>
      ) : null}
    </div>
  );
}

export default BotsView;

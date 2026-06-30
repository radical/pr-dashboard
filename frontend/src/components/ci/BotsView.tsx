import type {
  BotIssue,
  BotIssueGroup,
  BotPrNote,
  BotPullRequest,
  BotShepherdSnapshot,
  BotWorkQueueItem,
} from '../../types';
import { botBucketIds, bucketBotPrs, type BotBucket, type BotBucketId, type BotBucketTone } from '../../utils/botBuckets';
import { formatAge } from '../../utils/format';
import { buildRepoLabeler, relativeTime } from './ciFormat';
import { useCiHealth } from './useCiHealth';
import CiRefreshButton from './CiRefreshButton';

const severityTone: Record<string, BotBucketTone> = {
  broken: 'danger',
  attention: 'warning',
  standing: 'accent',
};

type RepoLabeler = (repo: string) => string;
type AgeLabeler = (repository: string, number: number) => string;

function noteFor(notes: BotPrNote[], repository: string, number: number) {
  return notes.find((note) => note.repository === repository && note.number === number);
}

// Top "Needs attention" lane — the prioritized work queue from the shepherd. 🧑 = human-only (merge /
// servicing / infra); 🤖 = a code change an agent could make.
function WorkQueue({ items, repoLabel, ageFor }: { items: BotWorkQueueItem[]; repoLabel: RepoLabeler; ageFor: AgeLabeler }) {
  return (
    <section className="ci-block bots-queue">
      <h3>⏳ Needs attention</h3>
      {items.length === 0 ? (
        <p className="ci-muted">Run the shepherd to prioritise what needs a human or an agent now.</p>
      ) : (
        <ul className="bots-queue-list">
          {items.map((item) => (
            <li key={`${item.repository}#${item.kind}#${item.number}`} className="bots-queue-item">
              <span className="bots-actor" title={item.humanOnly ? 'human-only' : 'agent-actionable'}>
                {item.humanOnly ? '🧑' : '🤖'}
              </span>
              <a className="bots-queue-link" href={item.htmlUrl} target="_blank" rel="noreferrer">
                #{item.number} {item.title} ↗
              </a>
              <span className="ci-muted bots-repo">{repoLabel(item.repository)}</span>
              <span className="bots-queue-action">{item.action}</span>
              <span className="ci-muted bots-age">{ageFor(item.repository, item.number)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// One deterministic PR bucket as a tone-colored attention card, enriched with the shepherd's per-PR
// why/action when available.
function PrBucketCard({ bucket, notes, repoLabel }: { bucket: BotBucket; notes: BotPrNote[]; repoLabel: RepoLabeler }) {
  return (
    <section className={`attention-card bots-card ${bucket.tone}`}>
      <div className="attention-card-header">
        <div className="attention-card-title"><span>{bucket.label}</span></div>
        <strong>{bucket.items.length}</strong>
      </div>
      <p className="ci-muted bots-card-summary">{bucket.summary}</p>
      <table className="ci-table">
        <thead><tr><th>PR</th><th>Repo</th><th>Why</th><th>Age</th></tr></thead>
        <tbody>
          {bucket.items.map(({ pr, reason }) => {
            const note = noteFor(notes, pr.repository, pr.number);
            return (
              <tr key={`${pr.repository}#${pr.number}`}>
                <td>
                  <a href={pr.htmlUrl} target="_blank" rel="noreferrer">#{pr.number} {pr.title} ↗</a>
                </td>
                <td>{repoLabel(pr.repository)}</td>
                <td>
                  <span className={`bots-reason ${bucket.tone}`}>{note?.why ?? reason}</span>
                  {note?.action ? <div className="ci-muted bots-card-action">→ {note.action}</div> : null}
                </td>
                <td className="ci-muted">{formatAge(pr.createdAt)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </section>
  );
}

// Shepherd-clustered issue groups: each a tone-colored card with a consolidation recommendation.
function IssueGroupCard({ group, repoLabel, ageFor }: { group: BotIssueGroup; repoLabel: RepoLabeler; ageFor: AgeLabeler }) {
  const issues = group.issues ?? [];
  return (
    <section className={`attention-card bots-card ${severityTone[group.severity] ?? 'accent'}`}>
      <div className="attention-card-header">
        <div className="attention-card-title"><span>{group.theme}</span></div>
        <strong>{issues.length}</strong>
      </div>
      <p className="bots-card-summary">{group.summary}</p>
      {group.recommendation ? <p className="ci-muted bots-card-action">→ {group.recommendation}</p> : null}
      <ul className="bots-issue-list">
        {issues.map((issue) => (
          <li key={`${issue.repository}#${issue.number}`}>
            <a href={issue.htmlUrl} target="_blank" rel="noreferrer">#{issue.number} {issue.title} ↗</a>
            <span className="ci-muted bots-repo">{repoLabel(issue.repository)} · {ageFor(issue.repository, issue.number)}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

// Flat issue table shown until the shepherd has clustered them.
function IssuesTable({ issues, repoLabel }: { issues: BotIssue[]; repoLabel: RepoLabeler }) {
  return (
    <table className="ci-table">
      <thead><tr><th>Title</th><th>Repo</th><th>Author</th><th>Age</th></tr></thead>
      <tbody>
        {issues.map((issue) => (
          <tr key={`${issue.repository}#${issue.number}`}>
            <td><a href={issue.htmlUrl} target="_blank" rel="noreferrer">#{issue.number} {issue.title} ↗</a></td>
            <td>{repoLabel(issue.repository)}</td>
            <td>{issue.author}</td>
            <td className="ci-muted">{formatAge(issue.createdAt)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function shepherdMeta(shepherd: BotShepherdSnapshot | null): string {
  if (!shepherd) {
    return '';
  }
  const cached = shepherd.fromCache ? ' (cached)' : '';
  return ` · shepherd ${relativeTime(shepherd.updatedAt)}${cached}`;
}

function BotsView() {
  const { data, error, refreshing, refreshError, refresh, shepherding, shepherdError, shepherd } = useCiHealth();

  if (error) {
    return <div className="ci-health-empty">Could not load bot activity: {error}</div>;
  }

  if (!data) {
    return <div className="ci-health-empty">Loading bot activity…</div>;
  }

  const { pulse, botShepherd } = data;
  const botPrs = pulse?.botPrs ?? [];
  const botIssues = pulse?.botIssues ?? [];
  const notes = botShepherd?.prNotes ?? [];
  const issueGroups = botShepherd?.issueGroups ?? [];

  // When the shepherd has run, let its per-PR bucket (a deeper read than the coarse pulse ciStatus)
  // place the row, so a PR never sits under "Easy wins" while its note says checks are failing.
  const validBuckets = new Set<string>(botBucketIds);
  const overrideBucket = (pr: BotPullRequest): BotBucketId | undefined => {
    const bucket = noteFor(notes, pr.repository, pr.number)?.bucket;
    return bucket && validBuckets.has(bucket) ? (bucket as BotBucketId) : undefined;
  };
  const buckets = bucketBotPrs(botPrs, notes.length > 0 ? overrideBucket : undefined);
  // The Bots page is for actionable PRs only: ready-to-merge ones already surface on the Review page's
  // bots lane, and pending ones are just waiting on checks. Show only Stuck + Broken here.
  const actionableBuckets = buckets.filter((bucket) => bucket.id === 'stuck' || bucket.id === 'broken');
  const actionableCount = actionableBuckets.reduce((total, bucket) => total + bucket.items.length, 0);
  const deferredCount = botPrs.length - actionableCount;

  const repoLabel = buildRepoLabeler([
    ...botPrs.map((b) => b.repository),
    ...botIssues.map((b) => b.repository),
    ...(botShepherd?.workQueue ?? []).map((b) => b.repository),
  ]);

  // Age is computed from the real createdAt (not the model's output, which can't do date math and would
  // also freeze in the cached snapshot), looked up by repository+number across PRs and issues.
  const createdAtByKey = new Map<string, string>();
  for (const pr of botPrs) {
    createdAtByKey.set(`${pr.repository}#${pr.number}`, pr.createdAt);
  }
  for (const issue of botIssues) {
    createdAtByKey.set(`${issue.repository}#${issue.number}`, issue.createdAt);
  }
  const ageFor: AgeLabeler = (repository, number) => {
    const createdAt = createdAtByKey.get(`${repository}#${number}`);
    return createdAt ? formatAge(createdAt) : '';
  };

  return (
    <div className="ci-health">
      <section className="ci-strip">
        <strong>🤖 Bot / automated activity</strong>
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

      <WorkQueue items={botShepherd?.workQueue ?? []} repoLabel={repoLabel} ageFor={ageFor} />

      <section className="bots-section">
        <h3>🔀 Bot PRs needing attention <span className="ci-muted">({actionableCount})</span></h3>
        {actionableCount === 0 ? (
          <p className="ci-empty">No bot PRs need attention — nothing stuck or broken.</p>
        ) : (
          <div className="bots-cards">
            {actionableBuckets.map((bucket) => (
              <PrBucketCard key={bucket.id} bucket={bucket} notes={notes} repoLabel={repoLabel} />
            ))}
          </div>
        )}
        {deferredCount > 0 ? (
          <p className="ci-muted">
            {deferredCount} ready-to-merge / pending bot PR{deferredCount === 1 ? '' : 's'} —{' '}
            <a href="?mode=review">see the Review page’s bots lane ↗</a>.
          </p>
        ) : null}
      </section>

      <section className="bots-section">
        <h3>📨 Auto-opened issues <span className="ci-muted">({botIssues.length})</span></h3>
        {botIssues.length === 0 ? (
          <p className="ci-empty">No open bot/automated issues.</p>
        ) : issueGroups.length > 0 ? (
          <div className="bots-cards">
            {issueGroups.map((group) => (
              <IssueGroupCard key={group.theme} group={group} repoLabel={repoLabel} ageFor={ageFor} />
            ))}
          </div>
        ) : (
          <IssuesTable issues={botIssues} repoLabel={repoLabel} />
        )}
      </section>
    </div>
  );
}

export default BotsView;

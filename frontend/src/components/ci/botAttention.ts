import type { BotIssue, BotIssueGroup, BotPrNote, BotPullRequest, BotWorkQueueItem, FailingWorkflow } from '../../types';
import { bucketBotPrs, type BotBucketId } from '../../utils/botBuckets';
import { classifyPattern, describePattern } from './ciPattern';

// One row in the Bots page's unified "Needs attention" table. Built deterministically from the snapshot
// so it renders before the (LLM) shepherd has run, then enriched by shepherd notes/queue when present.

export type BotActor = 'agent' | 'human';
export type BotTone = 'danger' | 'warning' | 'accent';
export type BotRowKind = 'pr' | 'issue' | 'ci';

export type BotAttentionRow = {
  key: string;
  kind: BotRowKind;
  actor: BotActor;
  title: string;
  htmlUrl: string | null;
  repository: string;
  why: string;
  tone: BotTone;
  action: string;
  context: string;
  // ISO timestamp the view formats with formatAge, or null when unknown (so the builder stays pure).
  createdAt: string | null;
};

export type LaneMeta = {
  topTier: boolean;
  sequence: { pass: boolean }[];
};

export type BuildBotRowsInput = {
  botPrs: BotPullRequest[];
  notes: BotPrNote[];
  workQueue: BotWorkQueueItem[];
  issueGroups: BotIssueGroup[];
  botIssues: BotIssue[];
  failingNow: FailingWorkflow[];
  // repo\nlane -> pulse-derived metadata, used to mark top-tier CI breaks and label their pattern.
  laneMeta: Map<string, LaneMeta>;
};

const TONE_RANK: Record<BotTone, number> = { danger: 3, warning: 2, accent: 1 };

function laneKey(repository: string, lane: string): string {
  return `${repository}\n${lane}`;
}

function noteFor(notes: BotPrNote[], repository: string, number: number): BotPrNote | undefined {
  return notes.find((note) => note.repository === repository && note.number === number);
}

// The shepherd's queue is the only source that knows human-vs-agent; fall back to a deterministic guess.
function actorForPr(
  bucket: BotBucketId,
  reason: string,
  queueByKey: Map<string, BotWorkQueueItem>,
  repository: string,
  number: number,
): BotActor {
  const queued = queueByKey.get(`${repository}#${number}`);
  if (queued) {
    return queued.humanOnly ? 'human' : 'agent';
  }
  // Conflicts need a human to resolve; failing checks and requested changes are code an agent can make.
  if (reason.includes('conflict')) {
    return 'human';
  }
  return bucket === 'broken' || reason.includes('changes requested') ? 'agent' : 'human';
}

function defaultPrAction(bucket: BotBucketId, reason: string): string {
  if (bucket === 'broken') {
    return 'Get checks green';
  }
  if (reason.includes('conflict')) {
    return 'Resolve the conflict, then it can merge';
  }
  if (reason.includes('changes requested')) {
    return 'Address review, re-push';
  }
  return 'Needs a human decision';
}

const SEVERITY_TONE: Record<string, BotTone> = { broken: 'danger', attention: 'warning', standing: 'accent' };

// Builds the ranked unified rows: unlinked top-tier CI breaks + stuck/broken bot PRs + auto-issue
// clusters, ordered by tone (danger first) then oldest-first.
export function buildBotAttentionRows(input: BuildBotRowsInput): BotAttentionRow[] {
  const rows: BotAttentionRow[] = [];
  const queueByKey = new Map(input.workQueue.map((item) => [`${item.repository}#${item.number}`, item] as const));

  // 1. Unlinked top-tier CI breaks become agent tasks (the CI -> Bots tie-in).
  for (const fail of input.failingNow) {
    const meta = input.laneMeta.get(laneKey(fail.repository, fail.lane));
    const topTier = meta?.topTier ?? fail.section === 'main';
    if (!topTier || !fail.likelyReal || fail.linkedIssue) {
      continue;
    }
    const pattern = describePattern(classifyPattern(meta?.sequence ?? []));
    rows.push({
      key: `ci:${fail.repository}/${fail.lane}`,
      kind: 'ci',
      actor: 'agent',
      title: `CI lane "${fail.lane}" red`,
      htmlUrl: fail.lastRunUrl,
      repository: fail.repository,
      why: 'no linked issue',
      tone: 'danger',
      action: 'Open tracking issue + investigate',
      context: `from CI Health · ${pattern.label} · ${fail.streak} build${fail.streak === 1 ? '' : 's'}`,
      createdAt: fail.failingSince,
    });
  }

  // 2. Bot PRs that need attention — only the actionable buckets (stuck / broken).
  const overrideBucket = (pr: BotPullRequest): BotBucketId | undefined => {
    const bucket = noteFor(input.notes, pr.repository, pr.number)?.bucket;
    return bucket === 'stuck' || bucket === 'broken' || bucket === 'easy-win' || bucket === 'pending'
      ? (bucket as BotBucketId)
      : undefined;
  };
  const buckets = bucketBotPrs(input.botPrs, input.notes.length > 0 ? overrideBucket : undefined);
  for (const bucket of buckets) {
    if (bucket.id !== 'stuck' && bucket.id !== 'broken') {
      continue;
    }
    const tone: BotTone = bucket.id === 'broken' ? 'danger' : 'warning';
    for (const { pr, reason } of bucket.items) {
      const note = noteFor(input.notes, pr.repository, pr.number);
      const why = note?.why ?? reason;
      rows.push({
        key: `pr:${pr.repository}#${pr.number}`,
        kind: 'pr',
        actor: actorForPr(bucket.id, reason, queueByKey, pr.repository, pr.number),
        title: `#${pr.number} ${pr.title}`,
        htmlUrl: pr.htmlUrl,
        repository: pr.repository,
        why,
        tone,
        action: note?.action ?? defaultPrAction(bucket.id, reason),
        context: pr.author,
        createdAt: pr.createdAt,
      });
    }
  }

  // 3. Auto-opened issues — one row per shepherd cluster, else one per raw issue.
  if (input.issueGroups.length > 0) {
    for (const group of input.issueGroups) {
      const issues = group.issues ?? [];
      rows.push({
        key: `grp:${group.theme}`,
        kind: 'issue',
        actor: 'human',
        title: issues.length > 1 ? `${issues.length} auto-issues: "${group.theme}"` : group.theme,
        htmlUrl: issues[0]?.htmlUrl ?? null,
        repository: issues[0]?.repository ?? '',
        why: issues.length > 1 ? 'duplicate cluster' : 'auto-issue',
        tone: SEVERITY_TONE[group.severity] ?? 'accent',
        action: group.recommendation || group.summary,
        context: group.summary,
        createdAt: null,
      });
    }
  } else {
    for (const issue of input.botIssues) {
      rows.push({
        key: `issue:${issue.repository}#${issue.number}`,
        kind: 'issue',
        actor: 'human',
        title: `#${issue.number} ${issue.title}`,
        htmlUrl: issue.htmlUrl,
        repository: issue.repository,
        why: 'auto-issue',
        tone: 'accent',
        action: 'Triage',
        context: issue.author,
        createdAt: issue.createdAt,
      });
    }
  }

  // Rank: most-severe tone first, then oldest first (ISO timestamps sort lexically; nulls last).
  return rows.sort((a, b) => {
    const tone = TONE_RANK[b.tone] - TONE_RANK[a.tone];
    if (tone !== 0) {
      return tone;
    }
    if (a.createdAt && b.createdAt) {
      return a.createdAt < b.createdAt ? -1 : a.createdAt > b.createdAt ? 1 : 0;
    }
    return a.createdAt ? -1 : b.createdAt ? 1 : 0;
  });
}

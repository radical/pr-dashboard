import type { BotPullRequest } from '../types';

// Deterministic, always-on bucketing of bot PRs from their merge-readiness state (no LLM). Mirrors the
// main page's "needs attention" lanes: each bucket has a tone + summary, and each item carries a one-line
// reason. The Copilot shepherd layer enriches these with prioritization + prose, but this works offline.

export type BotBucketTone = 'success' | 'warning' | 'danger' | 'accent';
export type BotBucketId = 'easy-win' | 'stuck' | 'broken' | 'pending';

export type BotPrRow = {
  pr: BotPullRequest;
  reason: string;
};

export type BotBucket = {
  id: BotBucketId;
  label: string;
  tone: BotBucketTone;
  summary: string;
  items: BotPrRow[];
};

type BotBucketSpec = {
  id: BotBucketId;
  label: string;
  tone: BotBucketTone;
  summary: string;
};

// Display order: ready-to-merge funnel first, then blocked, broken, and in-flight.
const bucketSpecs: BotBucketSpec[] = [
  { id: 'easy-win', label: '🟢 Easy wins', tone: 'success', summary: 'Green + mergeable — a human reviews & merges.' },
  { id: 'stuck', label: '🟠 Stuck', tone: 'warning', summary: 'Needs a human decision — conflict or requested changes.' },
  { id: 'broken', label: '🔴 Broken', tone: 'danger', summary: "Checks failing — can't merge until green." },
  { id: 'pending', label: '⏳ Pending', tone: 'accent', summary: 'Checks still running.' },
];

export function classifyBotPr(pr: BotPullRequest): { bucket: BotBucketId; reason: string } {
  if (pr.ciStatus === 'failing') {
    return { bucket: 'broken', reason: pr.mergeable === 'conflicting' ? 'CI failing + conflict' : 'CI failing' };
  }

  if (pr.mergeable === 'conflicting') {
    return { bucket: 'stuck', reason: pr.review === 'approved' ? 'approved but conflicting' : 'merge conflict' };
  }

  if (pr.review === 'changes_requested') {
    return { bucket: 'stuck', reason: 'changes requested' };
  }

  if (pr.ciStatus === 'pending') {
    return { bucket: 'pending', reason: 'checks running' };
  }

  return {
    bucket: 'easy-win',
    reason: pr.review === 'approved' ? 'approved + green — ready to merge' : 'green + mergeable — review & merge',
  };
}

// Groups PRs into the non-empty buckets, in display order. When the shepherd has run, `overrideBucket`
// lets its deeper per-PR read place the row (the deterministic ciStatus is coarser and can disagree).
export function bucketBotPrs(
  prs: BotPullRequest[],
  overrideBucket?: (pr: BotPullRequest) => BotBucketId | undefined,
): BotBucket[] {
  const rows = new Map<BotBucketId, BotPrRow[]>();
  for (const pr of prs) {
    const { bucket, reason } = classifyBotPr(pr);
    const id = overrideBucket?.(pr) ?? bucket;
    const list = rows.get(id) ?? [];
    list.push({ pr, reason });
    rows.set(id, list);
  }

  return bucketSpecs
    .map((spec) => ({ ...spec, items: rows.get(spec.id) ?? [] }))
    .filter((bucket) => bucket.items.length > 0);
}

export const botBucketIds: readonly BotBucketId[] = ['easy-win', 'stuck', 'broken', 'pending'];

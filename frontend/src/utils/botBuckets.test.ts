import { describe, expect, it } from 'vitest';
import type { BotPullRequest } from '../types';
import { bucketBotPrs, classifyBotPr } from './botBuckets';

function pr(overrides: Partial<BotPullRequest>): BotPullRequest {
  return {
    repository: 'microsoft/aspire',
    number: 1,
    title: 'PR',
    author: 'dependabot',
    htmlUrl: 'https://github.com/microsoft/aspire/pull/1',
    ciStatus: 'passing',
    mergeable: 'mergeable',
    review: 'review_required',
    labels: [],
    createdAt: '2026-06-20T00:00:00Z',
    updatedAt: '2026-06-29T00:00:00Z',
    ...overrides,
  };
}

describe('classifyBotPr', () => {
  it('flags failing checks as broken', () => {
    expect(classifyBotPr(pr({ ciStatus: 'failing' })).bucket).toBe('broken');
  });

  it('broken wins over conflict', () => {
    const r = classifyBotPr(pr({ ciStatus: 'failing', mergeable: 'conflicting' }));
    expect(r.bucket).toBe('broken');
    expect(r.reason).toContain('conflict');
  });

  it('flags conflicts as stuck', () => {
    expect(classifyBotPr(pr({ mergeable: 'conflicting' })).bucket).toBe('stuck');
  });

  it('approved-but-conflicting reads as stuck with the right reason', () => {
    const r = classifyBotPr(pr({ mergeable: 'conflicting', review: 'approved' }));
    expect(r.bucket).toBe('stuck');
    expect(r.reason).toBe('approved but conflicting');
  });

  it('flags requested changes as stuck', () => {
    expect(classifyBotPr(pr({ review: 'changes_requested' })).bucket).toBe('stuck');
  });

  it('flags running checks as pending', () => {
    expect(classifyBotPr(pr({ ciStatus: 'pending' })).bucket).toBe('pending');
  });

  it('green + mergeable is an easy win', () => {
    expect(classifyBotPr(pr({})).bucket).toBe('easy-win');
  });

  it('approved + green is ready to merge', () => {
    expect(classifyBotPr(pr({ review: 'approved' })).reason).toContain('ready to merge');
  });
});

describe('bucketBotPrs', () => {
  it('returns only non-empty buckets in display order', () => {
    const buckets = bucketBotPrs([
      pr({ number: 1, ciStatus: 'failing' }),
      pr({ number: 2 }),
      pr({ number: 3, mergeable: 'conflicting' }),
    ]);
    expect(buckets.map((b) => b.id)).toEqual(['easy-win', 'stuck', 'broken']);
    expect(buckets.find((b) => b.id === 'broken')?.items).toHaveLength(1);
  });

  it('returns no buckets when there are no PRs', () => {
    expect(bucketBotPrs([])).toEqual([]);
  });

  it('lets an override place a PR (shepherd bucket wins over coarse ciStatus)', () => {
    // ciStatus=passing classifies as easy-win, but the override moves it to broken.
    const buckets = bucketBotPrs([pr({ number: 1 })], () => 'broken');
    expect(buckets.map((b) => b.id)).toEqual(['broken']);
  });

  it('falls back to deterministic bucket when override returns undefined', () => {
    const buckets = bucketBotPrs([pr({ number: 1, mergeable: 'conflicting' })], () => undefined);
    expect(buckets.map((b) => b.id)).toEqual(['stuck']);
  });
});

import { describe, expect, it } from 'vitest';
import type { BotIssue, BotIssueGroup, BotPrNote, BotPullRequest, BotWorkQueueItem, FailingWorkflow } from '../../types';
import { buildBotAttentionRows, type BuildBotRowsInput, type LaneMeta } from './botAttention';

function pr(overrides: Partial<BotPullRequest>): BotPullRequest {
  return {
    repository: 'org/repo',
    number: 1,
    title: 'A bot PR',
    author: 'dependabot',
    htmlUrl: 'https://x/pr/1',
    ciStatus: 'success',
    mergeable: 'mergeable',
    review: 'none',
    labels: [],
    createdAt: '2026-06-20T00:00:00Z',
    updatedAt: '2026-06-20T00:00:00Z',
    ...overrides,
  };
}

function fail(overrides: Partial<FailingWorkflow>): FailingWorkflow {
  return {
    repository: 'org/repo',
    workflow: 'CI',
    lane: 'CI',
    section: 'main',
    failingSince: '2026-06-29T00:00:00Z',
    streak: 3,
    lastRunId: 42,
    lastRunUrl: 'https://x/run/42',
    likelyReal: true,
    linkedIssue: null,
    cadenceMinutes: 10,
    ...overrides,
  };
}

function input(overrides: Partial<BuildBotRowsInput>): BuildBotRowsInput {
  return {
    botPrs: [],
    notes: [],
    workQueue: [],
    issueGroups: [],
    botIssues: [],
    failingNow: [],
    laneMeta: new Map<string, LaneMeta>(),
    ...overrides,
  };
}

describe('buildBotAttentionRows — CI break tie-in', () => {
  it('turns an unlinked top-tier likely-real CI break into an agent task row', () => {
    const rows = buildBotAttentionRows(input({ failingNow: [fail({})] }));
    expect(rows).toHaveLength(1);
    expect(rows[0].kind).toBe('ci');
    expect(rows[0].actor).toBe('agent');
    expect(rows[0].why).toBe('no linked issue');
    expect(rows[0].action).toContain('Open tracking issue');
  });

  it('does not create a CI row when the lane already has a linked issue', () => {
    const rows = buildBotAttentionRows(input({ failingNow: [fail({ linkedIssue: '#9' })] }));
    expect(rows).toHaveLength(0);
  });

  it('does not create a CI row for a non-top-tier lane', () => {
    const laneMeta = new Map<string, LaneMeta>([['org/repo\nNightly', { topTier: false, sequence: [] }]]);
    const rows = buildBotAttentionRows(
      input({ failingNow: [fail({ lane: 'Nightly', section: 'scheduled' })], laneMeta }),
    );
    expect(rows).toHaveLength(0);
  });

  it('treats an always-show scheduled lane (top-tier via laneMeta) as a CI task', () => {
    const laneMeta = new Map<string, LaneMeta>([['org/repo\nOuterloop', { topTier: true, sequence: [] }]]);
    const rows = buildBotAttentionRows(
      input({ failingNow: [fail({ lane: 'Outerloop', section: 'scheduled' })], laneMeta }),
    );
    expect(rows).toHaveLength(1);
    expect(rows[0].kind).toBe('ci');
  });
});

describe('buildBotAttentionRows — bot PRs', () => {
  it('puts a CI-failing PR in a danger/agent row', () => {
    const rows = buildBotAttentionRows(input({ botPrs: [pr({ ciStatus: 'failing' })] }));
    expect(rows).toHaveLength(1);
    expect(rows[0].tone).toBe('danger');
    expect(rows[0].actor).toBe('agent');
  });

  it('puts a conflicting PR in a warning/human row', () => {
    const rows = buildBotAttentionRows(input({ botPrs: [pr({ mergeable: 'conflicting' })] }));
    expect(rows[0].tone).toBe('warning');
    expect(rows[0].actor).toBe('human');
  });

  it('excludes ready-to-merge and pending PRs from the attention list', () => {
    const rows = buildBotAttentionRows(
      input({ botPrs: [pr({ review: 'approved' }), pr({ number: 2, ciStatus: 'pending' })] }),
    );
    expect(rows).toHaveLength(0);
  });

  it('lets a shepherd note override the bucket, why, and action', () => {
    const notes: BotPrNote[] = [
      { repository: 'org/repo', number: 1, bucket: 'broken', why: 'segfault in setup', action: 'patch the harness' },
    ];
    const rows = buildBotAttentionRows(input({ botPrs: [pr({ ciStatus: 'success', review: 'approved' })], notes }));
    expect(rows).toHaveLength(1);
    expect(rows[0].tone).toBe('danger');
    expect(rows[0].why).toBe('segfault in setup');
    expect(rows[0].action).toBe('patch the harness');
  });

  it('lets the shepherd work queue decide the actor', () => {
    const workQueue: BotWorkQueueItem[] = [
      { repository: 'org/repo', kind: 'pr', number: 1, htmlUrl: 'x', title: 't', humanOnly: true, action: 'merge' },
    ];
    const rows = buildBotAttentionRows(input({ botPrs: [pr({ ciStatus: 'failing' })], workQueue }));
    expect(rows[0].actor).toBe('human');
  });
});

describe('buildBotAttentionRows — issues', () => {
  it('collapses a shepherd issue cluster into one row', () => {
    const issueGroups: BotIssueGroup[] = [
      {
        theme: 'nightly publish 403',
        severity: 'attention',
        summary: 'creds expired',
        recommendation: 'consolidate to #100',
        issues: [
          { repository: 'org/repo', number: 100, htmlUrl: 'x', title: 'a' },
          { repository: 'org/repo', number: 101, htmlUrl: 'y', title: 'b' },
        ],
      },
    ];
    const rows = buildBotAttentionRows(input({ issueGroups }));
    expect(rows).toHaveLength(1);
    expect(rows[0].kind).toBe('issue');
    expect(rows[0].why).toBe('duplicate cluster');
    expect(rows[0].tone).toBe('warning');
  });

  it('falls back to one row per raw issue when not yet clustered', () => {
    const botIssues: BotIssue[] = [
      {
        repository: 'org/repo',
        number: 5,
        title: 'broken thing',
        author: 'github-actions',
        htmlUrl: 'x',
        labels: [],
        createdAt: '2026-06-25T00:00:00Z',
        updatedAt: '2026-06-25T00:00:00Z',
      },
    ];
    const rows = buildBotAttentionRows(input({ botIssues }));
    expect(rows).toHaveLength(1);
    expect(rows[0].kind).toBe('issue');
  });
});

describe('buildBotAttentionRows — ranking', () => {
  it('orders danger before warning before accent, then oldest first', () => {
    const rows = buildBotAttentionRows(
      input({
        botPrs: [
          pr({ number: 1, mergeable: 'conflicting', createdAt: '2026-06-10T00:00:00Z' }), // warning, older
          pr({ number: 2, mergeable: 'conflicting', createdAt: '2026-06-28T00:00:00Z' }), // warning, newer
          pr({ number: 3, ciStatus: 'failing', createdAt: '2026-06-27T00:00:00Z' }), // danger
        ],
        botIssues: [
          {
            repository: 'org/repo',
            number: 9,
            title: 'i',
            author: 'github-actions',
            htmlUrl: 'x',
            labels: [],
            createdAt: '2026-06-01T00:00:00Z',
            updatedAt: '2026-06-01T00:00:00Z',
          },
        ],
      }),
    );
    expect(rows.map((r) => r.tone)).toEqual(['danger', 'warning', 'warning', 'accent']);
    // Within the warning tone, the older conflict (number 1) comes first.
    const warnings = rows.filter((r) => r.tone === 'warning');
    expect(warnings[0].title).toContain('#1');
  });
});

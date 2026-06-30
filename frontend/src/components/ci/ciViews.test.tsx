// @vitest-environment jsdom

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { CiHealthResponse, FailingWorkflow, WorkflowPulse } from '../../types';

type ActEnvironment = typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean };
(globalThis as ActEnvironment).IS_REACT_ACT_ENVIRONMENT = true;

// The views read everything through useCiHealth; mock it so we can render against a fixed snapshot.
let mockReturn: Record<string, unknown>;
vi.mock('./useCiHealth', () => ({ useCiHealth: () => mockReturn }));

const { default: CiHealthView } = await import('./CiHealthView');
const { default: BotsView } = await import('./BotsView');

function runRef(pass: boolean, runId: number) {
  return { pass, runId, url: `https://x/run/${runId}` };
}

// Newest-first: green for a while then a 3-build red tail => "newly red".
const newlyRedSequence = [
  runRef(false, 20), runRef(false, 19), runRef(false, 18),
  ...Array.from({ length: 15 }, (_, i) => runRef(true, 17 - i)),
];

function pulseLane(overrides: Partial<WorkflowPulse>): WorkflowPulse {
  return {
    repository: 'org/repo',
    workflow: 'CI',
    lane: 'CI',
    section: 'main',
    alwaysShow: false,
    runs: 18,
    passes: 15,
    passRate: 15 / 18,
    greenAtTip: false,
    sequence: newlyRedSequence,
    ...overrides,
  };
}

function failing(overrides: Partial<FailingWorkflow>): FailingWorkflow {
  return {
    repository: 'org/repo',
    workflow: 'CI',
    lane: 'CI',
    section: 'main',
    failingSince: '2026-06-30T12:00:00Z',
    streak: 3,
    lastRunId: 20,
    lastRunUrl: 'https://x/run/20',
    likelyReal: true,
    linkedIssue: null,
    cadenceMinutes: 10,
    ...overrides,
  };
}

function response(overrides: Partial<CiHealthResponse['pulse'] & object>): CiHealthResponse {
  return {
    pulse: {
      workflows: [pulseLane({})],
      failingNow: [failing({})],
      botPrs: [],
      botIssues: [],
      updatedAt: '2026-06-30T12:05:00Z',
      ...overrides,
    },
    weekly: {
      workflows: [
        { repository: 'org/repo', workflow: 'CI', lane: 'CI', section: 'main', alwaysShow: false, passRate: 0.83, priorPassRate: 0.9, delta: -0.07, dailyPassRates: [1, 1, 0.5, 1, 0, 1, 1], recentRuns: [] },
      ],
      updatedAt: '2026-06-30T09:00:00Z',
    },
    triage: null,
    botShepherd: null,
  };
}

function baseHook(data: CiHealthResponse) {
  return {
    data,
    error: null,
    refreshing: false,
    refreshError: null,
    refresh: () => {},
    triaging: false,
    triageError: null,
    triage: () => {},
    shepherding: false,
    shepherdError: null,
    shepherd: () => {},
  };
}

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
});

describe('CiHealthView (main/other split)', () => {
  it('puts a watched main build in the Main-builds card with a TOP pill, blocks sub-row, and pattern label', () => {
    mockReturn = baseHook(response({}));
    act(() => root.render(<CiHealthView />));

    const text = container.textContent ?? '';
    expect(text).toContain('Main builds on fire');
    expect(text).toContain('TOP');
    expect(text).toContain('push · main');
    expect(text).toContain('Recent runs');
    expect(text).toContain('newly red');
    // One block per run in the (sliced) sequence renders as a linked square.
    expect(container.querySelectorAll('.ci-subrow .ci-run').length).toBeGreaterThan(10);
    // The merged trends collapse into a single details element.
    expect(container.querySelectorAll('details.ci-trends').length).toBe(1);
  });

  it('routes an always-show scheduled lane to the Other-broken card without a TOP pill', () => {
    mockReturn = baseHook(
      response({
        workflows: [pulseLane({ lane: 'Outerloop', section: 'scheduled', alwaysShow: true })],
        failingNow: [failing({ lane: 'Outerloop', section: 'scheduled' })],
      }),
    );
    act(() => root.render(<CiHealthView />));
    const text = container.textContent ?? '';
    expect(text).toContain('Other broken lanes');
    expect(text).toContain('always-show');
    // It is not a watched main build, so no TOP pill and an empty main card.
    expect(text).not.toContain('TOP');
    expect(text).toContain('All watched main builds are green at tip.');
  });

  it('falls back to the weekly run history to classify a sparse failing lane (caching-safe #2)', () => {
    // Pulse window has only 2 runs (too few to classify), but the weekly fetch carries a long
    // newly-red history — the failing-now blocks/pattern should use the richer weekly source.
    const sparse = response({
      workflows: [pulseLane({ lane: 'Nightly', section: 'scheduled', alwaysShow: true, sequence: [runRef(false, 2), runRef(false, 1)] })],
      failingNow: [failing({ lane: 'Nightly', section: 'scheduled', streak: 2 })],
    });
    sparse.weekly = {
      workflows: [
        {
          repository: 'org/repo', workflow: 'CI', lane: 'Nightly', section: 'scheduled', alwaysShow: true,
          passRate: 0.85, priorPassRate: 0.9, delta: -0.05, dailyPassRates: [1, 1, 1, 1, 1, 0, 0],
          recentRuns: newlyRedSequence,
        },
      ],
      updatedAt: '2026-06-30T09:00:00Z',
    };
    mockReturn = baseHook(sparse);
    act(() => root.render(<CiHealthView />));
    // The richer weekly history (18 runs) drives the sub-row, not the 2-run pulse sequence.
    expect(container.querySelectorAll('.ci-subrow .ci-run').length).toBeGreaterThan(10);
    expect(container.textContent).toContain('newly red');
  });

  it('marks a real-failure verdict as agent-actionable and an infra verdict as human', () => {
    const data = response({
      workflows: [
        pulseLane({ lane: 'CI · main', section: 'main' }),
        pulseLane({ lane: 'CI · release/13.4', section: 'main' }),
      ],
      failingNow: [
        failing({ lane: 'CI · main', lastRunId: 20 }),
        failing({ lane: 'CI · release/13.4', lastRunId: 30 }),
      ],
    });
    data.triage = {
      items: [
        { repository: 'org/repo', workflow: 'CI', lane: 'CI · main', runId: 20, runUrl: 'https://x/run/20', failingSince: '2026-06-30T12:00:00Z', streak: 3, needsAction: true, category: 'real-failure', confidence: 'high', summary: 'test broke', suggestedAction: 'Fix the test', sameRootCauseAsPrevious: false, recurringBuilds: 1, triagedAt: '2026-06-30T12:05:00Z', model: 'x' },
        { repository: 'org/repo', workflow: 'CI', lane: 'CI · release/13.4', runId: 30, runUrl: 'https://x/run/30', failingSince: '2026-06-30T12:00:00Z', streak: 3, needsAction: true, category: 'infra', confidence: 'high', summary: 'runner died', suggestedAction: 'Stabilise the runner', sameRootCauseAsPrevious: false, recurringBuilds: 1, triagedAt: '2026-06-30T12:05:00Z', model: 'x' },
      ],
      updatedAt: '2026-06-30T12:06:00Z',
      error: null,
    };
    mockReturn = baseHook(data);
    act(() => root.render(<CiHealthView />));
    const text = container.textContent ?? '';
    expect(text).toContain('🤖');
    expect(text).toContain('🧑');
  });
});

describe('BotsView (Option 1)', () => {
  it('renders an unlinked top-tier CI break as an agent row in the unified table', () => {
    mockReturn = baseHook(response({}));
    act(() => root.render(<BotsView />));

    const text = container.textContent ?? '';
    expect(text).toContain('Needs attention');
    expect(text).toContain('CI lane "CI" red');
    expect(text).toContain('no linked issue');
    expect(text).toContain('from CI Health');
    // Agent actor marker present.
    expect(text).toContain('🤖');
  });
});

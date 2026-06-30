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
        { repository: 'org/repo', workflow: 'CI', lane: 'CI', section: 'main', alwaysShow: false, passRate: 0.83, priorPassRate: 0.9, delta: -0.07, dailyPassRates: [1, 1, 0.5, 1, 0, 1, 1] },
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

describe('CiHealthView (Option 2)', () => {
  it('pins a top-tier failing lane with a TOP pill, blocks sub-row, and pattern label', () => {
    mockReturn = baseHook(response({}));
    act(() => root.render(<CiHealthView />));

    const text = container.textContent ?? '';
    expect(text).toContain('Failing now');
    expect(text).toContain('TOP');
    expect(text).toContain('push · main');
    expect(text).toContain('Recent runs');
    expect(text).toContain('newly red');
    // One block per run in the (sliced) sequence renders as a linked square.
    expect(container.querySelectorAll('.ci-subrow .ci-run').length).toBeGreaterThan(10);
    // The merged trends collapse into a single details element.
    expect(container.querySelectorAll('details.ci-trends').length).toBe(1);
  });

  it('marks an always-show scheduled lane as top-tier', () => {
    mockReturn = baseHook(
      response({
        workflows: [pulseLane({ lane: 'Outerloop', section: 'scheduled', alwaysShow: true })],
        failingNow: [failing({ lane: 'Outerloop', section: 'scheduled' })],
      }),
    );
    act(() => root.render(<CiHealthView />));
    expect(container.textContent).toContain('TOP');
    expect(container.textContent).toContain('always-show');
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

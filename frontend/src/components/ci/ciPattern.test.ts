import { describe, expect, it } from 'vitest';
import { classifyPattern, describePattern } from './ciPattern';

// Helpers build a newest-first sequence. `seq('FFFPP')` => tip (index 0) failed, then F,F,P,P (older).
function seq(s: string): { pass: boolean }[] {
  return [...s].map((c) => ({ pass: c === 'P' }));
}

describe('classifyPattern', () => {
  it('returns healthy for an empty sequence', () => {
    expect(classifyPattern([]).kind).toBe('healthy');
  });

  it('returns healthy when every run passed', () => {
    expect(classifyPattern(seq('PPPPPPPP')).kind).toBe('healthy');
  });

  it('calls a short red tail after a green run "newly-red"', () => {
    // Green for a while, just broke 3 builds ago — the highest-urgency case.
    const result = classifyPattern(seq('FFFPPPPPPPPPPPPPPP'));
    expect(result.kind).toBe('newly-red');
    expect(result.failStreak).toBe(3);
  });

  it('calls a long red tail "chronic"', () => {
    const result = classifyPattern(seq('FFFFFFFPPPP'));
    expect(result.kind).toBe('chronic');
    expect(result.failStreak).toBe(7);
  });

  it('treats a single green blip inside a long red run as chronic (streak from tip)', () => {
    // Outerloop-style: red at tip for 9, one pass, then more red — leading streak >= 5.
    const result = classifyPattern(seq('FFFFFFFFFPFFFFFFFFFF'));
    expect(result.kind).toBe('chronic');
  });

  it('calls an alternating red-at-tip sequence "flaky"', () => {
    const result = classifyPattern(seq('FPPFPFPPFPPFPPF'));
    expect(result.kind).toBe('flaky');
  });

  it('calls a green-at-tip sequence with a recent red stretch "recovering"', () => {
    const result = classifyPattern(seq('PPPFFFFF'));
    expect(result.kind).toBe('recovering');
  });

  it('boundary: a red tail of 4 (green history) is newly-red, 5 is chronic', () => {
    expect(classifyPattern(seq('FFFFPPPPPPPP')).kind).toBe('newly-red');
    expect(classifyPattern(seq('FFFFFPPPPPPP')).kind).toBe('chronic');
  });

  it('a red tip with too little history to call green is newly-red, not chronic', () => {
    expect(classifyPattern(seq('FF')).kind).toBe('newly-red');
  });
});

describe('describePattern', () => {
  it('folds the build count into the chronic label', () => {
    expect(describePattern({ kind: 'chronic', failStreak: 7, failRate: 0.8 }).label).toBe('chronic · 7 builds');
  });

  it('singularizes the newly-red build count', () => {
    expect(describePattern({ kind: 'newly-red', failStreak: 1, failRate: 0.1 }).label).toBe('newly red · 1 build');
  });

  it('shows the fail rate for flaky', () => {
    const badge = describePattern({ kind: 'flaky', failStreak: 0, failRate: 0.4 });
    expect(badge.label).toBe('flaky · 40% fail');
    expect(badge.tone).toBe('warning');
  });
});

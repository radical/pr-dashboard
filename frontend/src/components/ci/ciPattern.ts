// Derives a single "pattern" label characterizing a lane's recent run history, so the CI page can show
// the *story* of a failure at a glance (newly broken vs chronic vs flaky) alongside the LLM verdict.
// Input is the lane's run sequence newest-first (matching WorkflowPulse.sequence). Pure + tunable.

export type PatternKind = 'newly-red' | 'chronic' | 'flaky' | 'recovering' | 'healthy';

export type PatternResult = {
  kind: PatternKind;
  // Leading run-streak matching the tip (consecutive fails when red at tip, else 0).
  failStreak: number;
  // Fraction of decided runs in the window that failed.
  failRate: number;
};

// Tunable thresholds — isolated so tuning against real data is a one-line change.
export const CHRONIC_STREAK = 5; // a red tip streak this long is a standing break, not a fresh one
export const MOSTLY_GREEN = 0.7; // pre-break history this green => the break is "new"
export const FLAKY_MIN_FAILRATE = 0.15;
export const FLAKY_MAX_FAILRATE = 0.85;
export const FLAKY_MIN_TRANSITION_RATE = 0.3; // share of adjacent runs that flip pass<->fail

export function classifyPattern(sequence: readonly { pass: boolean }[]): PatternResult {
  const n = sequence.length;
  if (n === 0) {
    return { kind: 'healthy', failStreak: 0, failRate: 0 };
  }

  const fails = sequence.reduce((count, run) => (run.pass ? count : count + 1), 0);
  const failRate = fails / n;

  let transitions = 0;
  for (let i = 1; i < n; i += 1) {
    if (sequence[i].pass !== sequence[i - 1].pass) {
      transitions += 1;
    }
  }
  const transitionRate = n > 1 ? transitions / (n - 1) : 0;
  const flakySignature =
    failRate >= FLAKY_MIN_FAILRATE && failRate <= FLAKY_MAX_FAILRATE && transitionRate >= FLAKY_MIN_TRANSITION_RATE;

  // Leading streak of runs matching the tip result.
  let leadStreak = 1;
  for (let i = 1; i < n; i += 1) {
    if (sequence[i].pass === sequence[0].pass) {
      leadStreak += 1;
    } else {
      break;
    }
  }

  const tipFailing = !sequence[0].pass;

  if (tipFailing) {
    if (leadStreak >= CHRONIC_STREAK) {
      return { kind: 'chronic', failStreak: leadStreak, failRate };
    }
    // History older than the red tail: if it was mostly green, this is a fresh break.
    const preBreak = sequence.slice(leadStreak);
    const preBreakPassRate = preBreak.length ? preBreak.filter((run) => run.pass).length / preBreak.length : 0;
    if (preBreak.length >= 2 && preBreakPassRate >= MOSTLY_GREEN) {
      return { kind: 'newly-red', failStreak: leadStreak, failRate };
    }
    if (flakySignature) {
      return { kind: 'flaky', failStreak: leadStreak, failRate };
    }
    // Short red tail with too little (or not-green-enough) history to call chronic.
    return { kind: 'newly-red', failStreak: leadStreak, failRate };
  }

  // Tip is green.
  if (fails === 0) {
    return { kind: 'healthy', failStreak: 0, failRate };
  }
  if (flakySignature) {
    return { kind: 'flaky', failStreak: 0, failRate };
  }
  // Green at tip but a recent red stretch that wasn't just noise — keep an eye on it.
  return { kind: 'recovering', failStreak: 0, failRate };
}

export type PatternBadge = {
  label: string;
  tone: 'danger' | 'warning' | 'success' | 'muted';
};

// Maps a result to the pill label + tone used on the page. Counts are folded into the label so the
// badge reads as a sentence fragment ("chronic · 7 builds").
export function describePattern(result: PatternResult): PatternBadge {
  switch (result.kind) {
    case 'newly-red':
      return { label: `newly red · ${result.failStreak} build${result.failStreak === 1 ? '' : 's'}`, tone: 'danger' };
    case 'chronic':
      return { label: `chronic · ${result.failStreak} builds`, tone: 'danger' };
    case 'flaky':
      return { label: `flaky · ${Math.round(result.failRate * 100)}% fail`, tone: 'warning' };
    case 'recovering':
      return { label: 'recovering', tone: 'success' };
    case 'healthy':
    default:
      return { label: 'healthy', tone: 'success' };
  }
}

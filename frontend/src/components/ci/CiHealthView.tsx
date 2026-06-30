import type { CiTriageItem, CiTriageSnapshot, FailingWorkflow, WorkflowPulse, WorkflowWeekly } from '../../types';
import { formatAge } from '../../utils/format';
import { buildRepoLabeler, delta, percent, relativeTime } from './ciFormat';
import { classifyPattern, describePattern } from './ciPattern';
import { useCiHealth } from './useCiHealth';
import CiRefreshButton from './CiRefreshButton';

const laneKey = (repository: string, lane: string) => `${repository}\n${lane}`;

// A lane is "top tier" when it's a push-triggered rolling build (main section) or a curated always-show
// scheduled lane — these dominate the page.
function isTopTier(pulse: WorkflowPulse | undefined, fallbackSection: string): boolean {
  if (pulse) {
    return pulse.section === 'main' || pulse.alwaysShow;
  }
  return fallbackSection === 'main';
}

// Per-run pass/fail blocks, oldest -> newest (left to right), each linking to its run.
function RunBlocks({ sequence }: { sequence: WorkflowPulse['sequence'] }) {
  if (sequence.length === 0) {
    return <span className="ci-muted">no recent runs</span>;
  }
  return (
    <span className="ci-runs">
      {[...sequence].slice(0, 24).reverse().map((run) => (
        <a
          key={run.runId}
          className={run.pass ? 'ci-run ci-run-pass' : 'ci-run ci-run-fail'}
          href={run.url}
          target="_blank"
          rel="noreferrer"
          title={`run ${run.runId} — ${run.pass ? 'passed' : 'failed'}`}
        />
      ))}
    </span>
  );
}

function PatternPill({ sequence }: { sequence: WorkflowPulse['sequence'] }) {
  const badge = describePattern(classifyPattern(sequence));
  return <span className={`ci-pat ${badge.tone}`}>{badge.label}</span>;
}

// Per-day pass-rate blocks for the 7d trend: green when the day was all-green, red when all-red, amber
// in between, and a faint placeholder for days with no decided runs.
function DailyBlocks({ rates }: { rates: number[] }) {
  return (
    <span className="ci-runs">
      {rates.map((rate, day) => {
        const cls = rate < 0 ? 'ci-day-empty' : rate >= 0.999 ? 'ci-run-pass' : rate <= 0.001 ? 'ci-run-fail' : 'ci-day-mixed';
        return <span key={day} className={`ci-run ${cls}`} title={rate < 0 ? 'no runs' : `${Math.round(rate * 100)}% pass`} />;
      })}
    </span>
  );
}

function cadenceLabel(minutes: number): string {
  if (!minutes) {
    return '';
  }
  return minutes % 60 === 0 ? `${minutes / 60}h` : `${minutes}m`;
}

// Verdict cell for one failing lane: the model's actionable/not call + reason, recurrence, and suggested
// action. "Analysing…" until a verdict for this exact run exists (auto-triage fills it in on cadence).
function VerdictCell({ verdict }: { verdict: CiTriageItem | undefined }) {
  if (!verdict) {
    return <span className="ci-muted">⏳ analysing…</span>;
  }
  const recurring = verdict.recurringBuilds > 1;
  return (
    <div>
      <div>
        <span className={`bots-reason ${verdict.needsAction ? 'danger' : 'success'}`}>
          {verdict.needsAction ? '⚠️ action' : '✅ no action'}
        </span>{' '}
        <span className="ci-muted">{verdict.category} · {verdict.confidence}</span>
      </div>
      <div>{verdict.summary}</div>
      {recurring ? (
        <div className="ci-muted" title="consecutive builds failing the same way">
          ↻ same failure ×{verdict.recurringBuilds}
        </div>
      ) : null}
      {verdict.needsAction && verdict.suggestedAction && verdict.suggestedAction.toLowerCase() !== 'none' ? (
        <div className="ci-muted">→ {verdict.suggestedAction}</div>
      ) : null}
    </div>
  );
}

type FailingRow = {
  failing: FailingWorkflow;
  pulse: WorkflowPulse | undefined;
  top: boolean;
  tone: 'top' | 'danger' | 'warning';
};

// "Failing now" — one ranked table. Top-tier lanes (push rolling + always-show) are pinned first and
// brightest; each lane gets a per-run blocks sub-row with a pattern label. Verdicts fill in from triage.
function FailingNowBlock({
  rows,
  triage,
  triaging,
  triageError,
  onRun,
  repoLabel,
}: {
  rows: FailingRow[];
  triage: CiTriageSnapshot | null;
  triaging: boolean;
  triageError: string | null;
  onRun: () => void;
  repoLabel: (repo: string) => string;
}) {
  const verdictByRun = new Map((triage?.items ?? []).map((it) => [it.runId, it]));
  const topCount = rows.filter((r) => r.top).length;

  return (
    <section className="ci-block">
      <h3>
        🚨 Failing now <span className="ci-muted">({rows.length})</span>
        {rows.length > 0 ? (
          <span className="ci-muted"> · {topCount} top-tier · {rows.length - topCount} other</span>
        ) : null}
        <button type="button" className="ci-refresh" onClick={onRun} disabled={triaging}>
          {triaging ? 'Triaging…' : '🤖 Run triage'}
        </button>
      </h3>
      {triageError ? <p className="ci-refresh-error">{triageError}</p> : null}
      {triage?.error ? <p className="ci-muted">Triage error: {triage.error}</p> : null}
      {rows.length === 0 ? (
        <p className="ci-empty">No lanes are red at tip. 🎉</p>
      ) : (
        <>
          <table className="ci-table ci-fail-table">
            <thead><tr><th></th><th>Lane</th><th>Repo</th><th>Failing</th><th>Assessment</th></tr></thead>
            <tbody>
              {rows.map(({ failing: f, pulse, top, tone }) => {
                const sequence = pulse?.sequence ?? [];
                const descriptor = top ? (pulse?.section === 'main' ? 'push · main' : 'always-show') : null;
                return [
                  <tr key={`${f.repository}/${f.lane}`} className={`ci-fail-row ${tone}`}>
                    <td>{top ? <span className="ci-pill-top">TOP</span> : <span title="red at tip">🔴</span>}</td>
                    <td>
                      <a href={f.lastRunUrl} target="_blank" rel="noreferrer">{f.lane} ↗</a>
                      {descriptor ? <> <span className="ci-muted">{descriptor}</span></> : null}
                    </td>
                    <td>{repoLabel(f.repository)}</td>
                    <td className="ci-muted">
                      {f.streak} build{f.streak === 1 ? '' : 's'} · {formatAge(f.failingSince)}
                      {f.cadenceMinutes ? <> · ~{cadenceLabel(f.cadenceMinutes)}</> : null}
                    </td>
                    <td><VerdictCell verdict={verdictByRun.get(f.lastRunId)} /></td>
                  </tr>,
                  <tr key={`${f.repository}/${f.lane}/blocks`} className={`ci-subrow ${tone}`}>
                    <td></td>
                    <td colSpan={4}>
                      <div className="ci-subrow-inner">
                        <span className="ci-subrow-lbl">Recent runs</span>
                        <RunBlocks sequence={sequence} />
                        {sequence.length > 0 ? <PatternPill sequence={sequence} /> : null}
                      </div>
                    </td>
                  </tr>,
                ];
              })}
            </tbody>
          </table>
          {triage ? <p className="ci-strip-meta">triage {relativeTime(triage.updatedAt)}</p> : null}
        </>
      )}
    </section>
  );
}

// Merged, de-emphasized "trends & healthy lanes": the old 36h/7d × main/scheduled tables collapse into
// one details block, summarized in the summary line, with degrading lanes (negative delta) sorted first.
function TrendsDetails({ weekly, greenCount, repoLabel }: { weekly: WorkflowWeekly[]; greenCount: number; repoLabel: (repo: string) => string }) {
  const sorted = [...weekly].sort((a, b) => a.delta - b.delta);
  const avgPass = weekly.length ? weekly.reduce((sum, w) => sum + w.passRate, 0) / weekly.length : 1;
  return (
    <details className="ci-trends">
      <summary>
        🩺 Trends &amp; healthy lanes <span className="ci-muted">— {greenCount} green at tip · 7d pass {percent(avgPass)}</span>
      </summary>
      {sorted.length === 0 ? (
        <p className="ci-empty">No trend data yet.</p>
      ) : (
        <table className="ci-table">
          <thead><tr><th>Repo</th><th>Lane</th><th>7d daily</th><th>7d pass</th><th>vs prior</th></tr></thead>
          <tbody>
            {sorted.map((w) => (
              <tr key={`${w.repository}/${w.lane}`} className={w.delta < 0 ? 'ci-degrading' : undefined}>
                <td>{repoLabel(w.repository)}</td>
                <td>{w.lane}</td>
                <td><DailyBlocks rates={w.dailyPassRates} /></td>
                <td>{percent(w.passRate)}</td>
                <td>{delta(w.delta)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </details>
  );
}

function CiHealthView() {
  const { data, error, refreshing, refreshError, refresh, triaging, triageError, triage } = useCiHealth();

  if (error) {
    return <div className="ci-health-empty">Could not load CI health: {error}</div>;
  }

  if (!data) {
    return <div className="ci-health-empty">Loading CI health…</div>;
  }

  const { pulse, weekly } = data;
  const workflows = pulse?.workflows ?? [];
  const pulseByLane = new Map(workflows.map((w) => [laneKey(w.repository, w.lane), w]));
  const weeklyWorkflows = weekly?.workflows ?? [];
  const repoLabel = buildRepoLabeler([
    ...workflows.map((w) => w.repository),
    ...weeklyWorkflows.map((w) => w.repository),
  ]);

  // Build + rank the failing rows: top-tier first, then likely-real, then longest streak.
  const failingRows: FailingRow[] = (pulse?.failingNow ?? [])
    .map((failing) => {
      const lanePulse = pulseByLane.get(laneKey(failing.repository, failing.lane));
      const top = isTopTier(lanePulse, failing.section);
      const tone: FailingRow['tone'] = top ? 'top' : failing.likelyReal ? 'danger' : 'warning';
      return { failing, pulse: lanePulse, top, tone };
    })
    .sort((a, b) =>
      Number(b.top) - Number(a.top) ||
      Number(b.failing.likelyReal) - Number(a.failing.likelyReal) ||
      b.failing.streak - a.failing.streak);

  const greenCount = workflows.filter((w) => w.greenAtTip).length;
  const redCount = failingRows.length;
  const topRed = failingRows.filter((r) => r.top).length;

  return (
    <div className="ci-health">
      <section className="ci-strip">
        <strong>{redCount === 0 ? '🟢 CI: all lanes green at tip' : `🟡 CI: ${redCount} lane${redCount === 1 ? '' : 's'} red at tip`}</strong>
        <span className="ci-strip-meta">
          {redCount > 0 ? <>{topRed} top-tier · {redCount - topRed} other · </> : null}
          {pulse ? `pulse ${relativeTime(pulse.updatedAt)}` : 'pulse pending'} ·{' '}
          {weekly ? `weekly ${relativeTime(weekly.updatedAt)}` : 'weekly pending'}
        </span>
        <CiRefreshButton refreshing={refreshing} refreshError={refreshError} onRefresh={refresh} />
      </section>

      <FailingNowBlock
        rows={failingRows}
        triage={data.triage}
        triaging={triaging}
        triageError={triageError}
        onRun={triage}
        repoLabel={repoLabel}
      />

      <TrendsDetails weekly={weeklyWorkflows} greenCount={greenCount} repoLabel={repoLabel} />
    </div>
  );
}

export default CiHealthView;

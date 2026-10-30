import { useQueryClient } from '@tanstack/react-query';
import { History, Plus, Search } from 'lucide-react';
import { useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { isApiError } from '@/api/client';
import {
  isInFlight,
  useDigest,
  useJobs,
  useLatestDigest,
  usePreferences,
  useResumes,
  useStartRun,
  type JobSummary,
} from '@/api/queries';
import { useSignedInMe } from '@/app/signedIn';
import { Alert, StatusPill } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import { salaryShort, stamp } from '@/lib/format';
import { cn } from '@/lib/utils';
import { EligibilityChip } from './EligibilityChip';
import { JobDetailPane } from './JobDetailPane';

type EligibilityFilter = 'all' | 'india_eligible' | 'worldwide' | 'unclear';

const pills: { value: EligibilityFilter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'india_eligible', label: 'India-eligible' },
  { value: 'worldwide', label: 'Worldwide' },
  { value: 'unclear', label: 'Unclear' },
];

/** Shortcuts that act on the selected job; features that arrive later answer with a short note. */
const later: Record<string, string> = {
  s: 'Saving to the tracker is coming soon',
  t: 'Résumé tailoring is coming soon',
};

/**
 * Today's shortlist (design/tailorly-app.html #shortlist): jobs scored for the user, best fit first, in a master-detail
 * layout. Filters by eligibility, minimum score (defaults to the user's preference) and text; says how many jobs sit
 * below the minimum. Keyboard: / filter, j/k move, o open the source's apply link, Esc back to the list. "Run now"
 * starts a manual search (3 a day) and shows its queued → running → ready/failed state.
 */
export function ShortlistPage() {
  const me = useSignedInMe();
  const say = useToast();
  const qc = useQueryClient();
  const [params, setParams] = useSearchParams();
  const prefs = usePreferences(me.emailVerified);
  const resumes = useResumes(me.emailVerified);
  const latest = useLatestDigest(me.emailVerified);

  const [eligibility, setEligibility] = useState<EligibilityFilter>('all');
  const [minChoice, setMinChoice] = useState<number | null>(null);
  const [q, setQ] = useState('');
  const text = useDeferredValue(q.trim());
  const userMin = prefs.data?.minMatchScore ?? 8;
  const min = minChoice ?? userMin;
  const filters = { eligibility: eligibility === 'all' ? undefined : eligibility, q: text };
  const ready = prefs.isSuccess;
  const jobs = useJobs({ ...filters, minScore: min }, ready);
  const unfiltered = useJobs({ ...filters, minScore: 1 }, ready && min > 1);
  const hidden = min > 1 && jobs.data && unfiltered.data ? Math.max(0, unfiltered.data.total - jobs.data.total) : 0;
  const items = useMemo(() => jobs.data?.items ?? [], [jobs.data]);

  const selectedParam = params.get('job');
  const selectedId = selectedParam ?? items[0]?.id ?? null;
  const [detailOpen, setDetailOpen] = useState(selectedParam !== null);
  const select = (id: string, open = false) => {
    setParams(
      (p) => {
        p.set('job', id);
        return p;
      },
      { replace: true },
    );
    if (open) setDetailOpen(true);
  };

  // "Run now": poll the run until it finishes, then refresh the list.
  const startRun = useStartRun();
  const [run, setRun] = useState<{ id: string; startedAt: number } | null>(null);
  const runQuery = useDigest(run?.id ?? null, run?.startedAt ?? 0);
  const runStatus = runQuery.data?.status;
  const reported = useRef<string | null>(null);
  useEffect(() => {
    if (!run || !runQuery.data || isInFlight(runStatus) || reported.current === run.id) return;
    reported.current = run.id;
    void qc.invalidateQueries({ queryKey: ['jobs'] });
    void qc.invalidateQueries({ queryKey: ['digests'] });
    if (runStatus === 'ready') {
      const n = runQuery.data.items.length;
      say(n === 0 ? 'Search finished: no new matches' : `Search finished: ${n} ${n === 1 ? 'match' : 'matches'}`);
    }
  }, [run, runQuery.data, runStatus, qc, say]);
  const onRunNow = () =>
    startRun.mutate(undefined, {
      onSuccess: (d) => setRun({ id: d.id, startedAt: Date.now() }),
    });
  const runError = startRun.error && isApiError(startRun.error) ? startRun.error : null;

  // Keyboard shortcuts (prototype): typing in a field never triggers them.
  const searchRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const typing = /^(INPUT|SELECT|TEXTAREA)$/.test((document.activeElement as HTMLElement | null)?.tagName ?? '');
      if (e.key === '/' && !typing) {
        e.preventDefault();
        searchRef.current?.focus();
        return;
      }
      if (e.key === 'Escape') {
        if (typing) (document.activeElement as HTMLElement).blur();
        setDetailOpen(false);
        return;
      }
      if (typing || e.metaKey || e.ctrlKey || e.altKey) return;
      const i = items.findIndex((j) => j.id === selectedId);
      if (e.key === 'j' && i < items.length - 1) select(items[i + 1].id);
      else if (e.key === 'k' && i > 0) select(items[i - 1].id);
      else if (e.key === 'o' && i >= 0) window.open(items[i].applyUrl, '_blank', 'noopener,noreferrer');
      else if (later[e.key]) say(later[e.key]);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  });
  useEffect(() => {
    listRef.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
  }, [selectedId]);

  const hasResume = resumes.data?.some((r) => r.isActive && r.parseStatus === 'ready') ?? true;
  const minOptions = [...new Set([6, 7, 8, 9, userMin])].sort((a, b) => a - b);
  const running = !!run && (startRun.isPending || isInFlight(runStatus));
  const selectedInList = items.find((j) => j.id === selectedId) ?? null;

  return (
    <div className={cn('ms', detailOpen && 'detail-open')}>
      <section className="listcol" aria-label="Shortlist">
        <div className="lhead">
          <div className="row between" style={{ alignItems: 'flex-start' }}>
            <div>
              <h1>Today’s shortlist</h1>
              <div className="sub font-mono">
                {latest.data
                  ? stamp(latest.data.finishedAt ?? latest.data.createdAt, me.timeZone) +
                    (latest.data.kind === 'manual' ? ' · Run now' : '')
                  : 'No digest yet'}
              </div>
            </div>
            <div className="row" style={{ gap: 6 }}>
              <Button
                size="sm"
                onClick={onRunNow}
                disabled={running || !hasResume}
                title="Search new jobs now (3 a day)"
              >
                <Search size={14} aria-hidden="true" /> {running ? 'Running…' : 'Run now'}
              </Button>
              <Button asChild size="sm" variant="ghost" title="Digest history">
                <Link to="/digests" aria-label="Digest history">
                  <History size={14} aria-hidden="true" />
                </Link>
              </Button>
              <Button asChild size="sm">
                <Link to="/add-job">
                  <Plus size={14} aria-hidden="true" /> Add job
                </Link>
              </Button>
            </div>
          </div>

          {run && runQuery.data && (isInFlight(runStatus) || runStatus === 'failed') && (
            <div className="row" style={{ gap: 8 }} aria-live="polite">
              <StatusPill status={runQuery.data.status} />
              <span className="help">
                {runStatus === 'failed'
                  ? 'The search failed. Try again later.'
                  : 'Searching new jobs and scoring them…'}
              </span>
            </div>
          )}
          {runError && (
            <Alert tone={runError.code === 'RUN_LIMIT_REACHED' ? 'info' : 'bad'}>{runError.userMessage}</Alert>
          )}
          {latest.data?.degraded && (
            <Alert tone="info">
              The last run ranked some jobs by title and skills only, without a fit score (AI scoring was paused or your
              Discovery budget is used up).
            </Alert>
          )}

          <div className="searchbox">
            <input
              ref={searchRef}
              className="input"
              type="search"
              placeholder="Filter by title or company"
              aria-label="Filter jobs"
              value={q}
              maxLength={100}
              onChange={(e) => setQ(e.target.value)}
            />
            <span className="kbd" aria-hidden="true">
              /
            </span>
          </div>
          <div className="pills" role="group" aria-label="Eligibility filter">
            {pills.map((p) => (
              <button
                key={p.value}
                type="button"
                className="pill"
                aria-pressed={eligibility === p.value}
                onClick={() => setEligibility(p.value)}
              >
                {p.label}
              </button>
            ))}
            <label className="text-muted" style={{ marginLeft: 'auto' }}>
              min{' '}
              <select
                className="select"
                aria-label="Minimum match score"
                style={{ width: 'auto', padding: '2px 6px' }}
                value={min}
                onChange={(e) => setMinChoice(Number(e.target.value))}
              >
                {minOptions.map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            </label>
          </div>
        </div>

        <div className="lscroll" ref={listRef}>
          {!hasResume ? (
            <div className="emptyrow">
              Your shortlist starts with your résumé. <Link to="/resume">Upload it on the Résumé page</Link>, then run a
              search or wait for your daily digest.
            </div>
          ) : jobs.isPending && ready ? (
            <div className="emptyrow" aria-busy="true">
              Loading your shortlist…
            </div>
          ) : jobs.isError ? (
            <div className="emptyrow" role="alert">
              {isApiError(jobs.error) ? jobs.error.userMessage : 'Could not load your shortlist.'}
            </div>
          ) : items.length === 0 ? (
            <div className="emptyrow">
              {latest.data || filters.eligibility || text || minChoice !== null
                ? 'No jobs match. Lower the minimum score or clear the filters.'
                : `No shortlist yet. Your daily digest runs at ${prefs.data?.scheduleLocalTime ?? '18:00'} (${me.timeZone}), or press Run now.`}
            </div>
          ) : (
            <div role="listbox" aria-label="Jobs">
              {items.map((j) => (
                <JobRow key={j.id} job={j} selected={j.id === selectedId} onSelect={() => select(j.id, true)} />
              ))}
            </div>
          )}
          {hidden > 0 && (
            <div className="hiddenrow">
              {hidden} more below your minimum score of {min}.
            </div>
          )}
          {jobs.data && jobs.data.total > items.length && (
            <div className="hiddenrow">
              Showing the best {items.length} of {jobs.data.total}. Raise the minimum score or filter to see others.
            </div>
          )}
        </div>
        <div className="lfoot">
          <span>
            <span className="kbd">j</span> <span className="kbd">k</span> move
          </span>
          <span>
            <span className="kbd">/</span> filter
          </span>
          <span>
            <span className="kbd">o</span> open source
          </span>
          <span>
            <span className="kbd">Esc</span> back
          </span>
        </div>
      </section>

      <section className="detail" aria-label="Job detail">
        <JobDetailPane id={selectedId} fallback={selectedInList} onBack={() => setDetailOpen(false)} />
      </section>
    </div>
  );
}

function JobRow({ job, selected, onSelect }: { job: JobSummary; selected: boolean; onSelect: () => void }) {
  const fit = job.score?.fitScore;
  return (
    <button type="button" className="jrow" role="option" aria-selected={selected} onClick={onSelect}>
      <div
        className={cn('sc', fit !== undefined && fit >= 9 && 'hi')}
        title={fit === undefined ? 'Not scored' : `Fit ${fit} of 10`}
      >
        {fit ?? '–'}
      </div>
      <div>
        <div className="rt">{job.title}</div>
        <div className="rm">
          {job.companyName}
          {job.locationText ? ` · ${job.locationText}` : ''}
        </div>
        <div className="rchips">
          <EligibilityChip eligibility={job.eligibility} />
          <span className="chip">{salaryShort(job)}</span>
          {!job.score && <span className="chip">not scored</span>}
          {/* The followed attribution link is in the detail pane (a link cannot sit inside this row button). */}
          <span className="chip">via {job.source.name}</span>
        </div>
      </div>
    </button>
  );
}

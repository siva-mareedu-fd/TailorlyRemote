import { ChevronLeft } from 'lucide-react';
import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { isApiError } from '@/api/client';
import { useDigest, useDigests, type DigestSummary } from '@/api/queries';
import { useSignedInMe } from '@/app/signedIn';
import { Alert, Skeleton, StatusPill } from '@/components/Feedback';
import { Segmented } from '@/components/Controls';
import { Button } from '@/components/ui/button';
import { EligibilityChip } from '@/features/shortlist/EligibilityChip';
import { localDate, salaryShort, stamp } from '@/lib/format';

const kinds = [
  { value: 'all', label: 'All' },
  { value: 'scheduled', label: 'Daily digests' },
  { value: 'manual', label: 'Run now' },
] as const;

type Kind = (typeof kinds)[number]['value'];

const errorText: Record<string, string> = {
  TIMEOUT: 'did not finish',
  AI_UNAVAILABLE: 'AI unavailable',
  RESUME_REQUIRED: 'no active résumé',
  INTERNAL_ERROR: 'failed',
};

/**
 * Digest and run history (PRD F7 "digest history, run history"). Not in the prototype: built from its page, table,
 * segmented control and status-pill components. Each row opens that run's ranked jobs.
 */
export function DigestsPage() {
  const me = useSignedInMe();
  const [kind, setKind] = useState<Kind>('all');
  const [page, setPage] = useState(1);
  const digests = useDigests(page, kind === 'all' ? undefined : kind);
  const pages = digests.data ? Math.max(1, Math.ceil(digests.data.total / digests.data.pageSize)) : 1;

  return (
    <div className="page">
      <div className="page-title">
        <div>
          <h1>Digest history</h1>
          <div className="sub">
            Your daily digests and the searches you ran. Runs and their results are kept for 12 months.
          </div>
        </div>
        <Segmented
          label="Kind"
          value={kind}
          options={[...kinds]}
          onChange={(k) => {
            setKind(k);
            setPage(1);
          }}
        />
      </div>
      {digests.isPending ? (
        <Skeleton lines={4} />
      ) : digests.isError ? (
        <Alert tone="bad">
          {isApiError(digests.error) ? digests.error.userMessage : 'Could not load your history.'}
        </Alert>
      ) : digests.data.items.length === 0 ? (
        <div className="empty">
          No runs yet. Your daily digest arrives at your scheduled time; you can also press Run now on the{' '}
          <Link to="/shortlist">Shortlist</Link>.
        </div>
      ) : (
        <>
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Type</th>
                  <th>Status</th>
                  <th>Matches</th>
                  <th>New jobs checked</th>
                  <th>Emailed</th>
                </tr>
              </thead>
              <tbody>
                {digests.data.items.map((d) => (
                  <DigestRow key={d.id} digest={d} timeZone={me.timeZone} />
                ))}
              </tbody>
            </table>
          </div>
          {pages > 1 && (
            <div className="row between mt-3">
              <Button size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
                Newer
              </Button>
              <span className="help">
                Page {page} of {pages}
              </span>
              <Button size="sm" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
                Older
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function DigestRow({ digest: d, timeZone }: { digest: DigestSummary; timeZone: string }) {
  return (
    <tr>
      <td>
        <Link to={`/digests/${d.id}`}>{localDate(d.localDate)}</Link>
        {d.startedAt && <div className="help font-mono">{stamp(d.startedAt, timeZone)}</div>}
      </td>
      <td>{d.kind === 'scheduled' ? 'Daily digest' : 'Run now'}</td>
      <td>
        <div className="row" style={{ gap: 6 }}>
          <StatusPill status={d.status} />
          {d.degraded && (
            <span className="chip warn" title="Some jobs were ranked without a fit score">
              rules only
            </span>
          )}
          {d.status === 'failed' && d.errorCode && (
            <span className="help">{errorText[d.errorCode] ?? d.errorCode}</span>
          )}
        </div>
      </td>
      <td className="font-mono">{d.status === 'ready' ? d.itemCount : '–'}</td>
      <td className="font-mono">{d.jobsConsidered ?? '–'}</td>
      <td className="text-muted">
        {d.emailedAt ? stamp(d.emailedAt, timeZone) : d.kind === 'manual' ? 'never (Run now)' : '–'}
      </td>
    </tr>
  );
}

/** One run: its ranked jobs, each linking to the Shortlist with that job open. */
export function DigestDetailPage() {
  const { id } = useParams();
  const me = useSignedInMe();
  const [openedAt] = useState(() => Date.now());
  const digest = useDigest(id ?? null, openedAt);

  return (
    <div className="page">
      <Link to="/digests" className="row mb-3" style={{ gap: 4 }}>
        <ChevronLeft size={14} aria-hidden="true" /> Digest history
      </Link>
      {digest.isPending ? (
        <Skeleton lines={4} />
      ) : digest.isError ? (
        <Alert tone="bad">
          {isApiError(digest.error) && digest.error.status === 404
            ? 'This digest does not exist.'
            : 'Could not load this digest.'}
        </Alert>
      ) : (
        <>
          <div className="page-title">
            <div>
              <h1>
                {digest.data.kind === 'scheduled' ? 'Daily digest' : 'Run now'} · {localDate(digest.data.localDate)}
              </h1>
              <div className="sub">
                {digest.data.finishedAt ? `Finished ${stamp(digest.data.finishedAt, me.timeZone)}` : 'Not finished'}
                {digest.data.emailedAt ? ` · emailed ${stamp(digest.data.emailedAt, me.timeZone)}` : ''}
              </div>
            </div>
            <StatusPill status={digest.data.status} />
          </div>
          {digest.data.degraded && (
            <Alert tone="info" className="mb-3">
              Some jobs in this run were ranked by title and skills only, without a fit score.
            </Alert>
          )}
          {digest.data.items.length === 0 ? (
            <div className="empty">
              {digest.data.status === 'ready'
                ? 'No jobs matched your preferences in this run.'
                : 'This run has no results.'}
            </div>
          ) : (
            <div className="tablewrap">
              <table>
                <thead>
                  <tr>
                    <th>#</th>
                    <th>Role</th>
                    <th>Company</th>
                    <th>Fit</th>
                    <th>Eligibility</th>
                    <th>Salary</th>
                    <th>Source</th>
                  </tr>
                </thead>
                <tbody>
                  {digest.data.items.map(({ rank, job }) => (
                    <tr key={job.id}>
                      <td className="font-mono">{rank}</td>
                      <td>
                        <Link to={`/shortlist?job=${job.id}`}>{job.title}</Link>
                      </td>
                      <td>{job.companyName}</td>
                      <td className="font-mono">{job.score?.fitScore ?? '–'}</td>
                      <td>
                        <EligibilityChip eligibility={job.eligibility} />
                      </td>
                      <td>{salaryShort(job)}</td>
                      <td>
                        <a href={job.source.sourceUrl} target="_blank" rel="noopener">
                          via {job.source.name}
                        </a>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </div>
  );
}

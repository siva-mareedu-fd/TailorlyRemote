import { ExternalLink, TriangleAlert } from 'lucide-react';
import { isApiError } from '@/api/client';
import { useJob, type JobDetail, type JobSummary } from '@/api/queries';
import { Skeleton } from '@/components/Feedback';
import { Button, buttonVariants } from '@/components/ui/button';
import { ago, approxInr, host, salaryShort } from '@/lib/format';
import { cn } from '@/lib/utils';
import { EligibilityChip } from './EligibilityChip';

const confidenceTone: Record<string, string> = { high: 'ok', medium: '', low: 'warn' };

/**
 * The right-hand pane of the Shortlist (prototype `renderDetail`): fit and reasons, skills against the résumé,
 * eligibility with its evidence, the role summary and description, compensation and source attribution. Apply opens the
 * source-supplied apply URL in a new tab; the attribution is a normal followed link to the listing (PRD F14).
 */
export function JobDetailPane({
  id,
  fallback,
  onBack,
}: {
  id: string | null;
  fallback: JobSummary | null;
  onBack: () => void;
}) {
  const detail = useJob(id);
  const job: (JobSummary & Partial<JobDetail>) | null = detail.data ?? fallback;

  if (!id || (!job && !detail.isPending))
    return (
      <div className="dscroll">
        <div className="emptyrow">
          {detail.error && isApiError(detail.error) && detail.error.status === 404
            ? 'This job is no longer available.'
            : 'Select a job to see details.'}
        </div>
      </div>
    );
  if (!job)
    return (
      <div className="dscroll" aria-busy="true">
        <Skeleton lines={5} />
      </div>
    );

  const score = job.score;
  const applyLabel = job.source.attributionText ? job.source.name : host(job.applyUrl);
  const matched = score?.matchedSkills ?? [];
  const missing = score?.missingSkills ?? [];
  const required = (job.skillsRequired ?? []).filter(
    (s) =>
      !matched.some((m) => m.toLowerCase() === s.toLowerCase()) &&
      !missing.some((m) => m.toLowerCase() === s.toLowerCase()),
  );

  return (
    <div className="dscroll">
      <div className="dwrap">
        <button className="back" type="button" onClick={onBack}>
          ‹ Back to list
        </button>
        <div className="crumb font-mono">
          shortlist / {job.companyName.toLowerCase()} / {job.source.key}
        </div>
        <h2 className="dh">{job.title}</h2>
        <div className="sub" style={{ fontSize: 14 }}>
          {job.companyName}
          {job.locationText ? ` · ${job.locationText}` : ''}
        </div>
        <div className="row" style={{ margin: '14px 0 6px', gap: 6 }}>
          <EligibilityChip eligibility={job.eligibility} />
          <span className="chip">{salaryShort(job)}</span>
          <span className="chip">posted {ago(job.postedAt)}</span>
        </div>

        <div className="row" style={{ margin: '12px 0 20px' }}>
          <Button variant="primary" disabled title="Coming soon">
            Tailor résumé{' '}
            <span
              className="kbd"
              style={{ color: 'inherit', borderColor: 'currentColor', background: 'transparent', opacity: 0.8 }}
            >
              t
            </span>
          </Button>
          <Button disabled title="Coming soon">
            Cover letter
          </Button>
          <Button disabled title="Coming soon">
            Save <span className="kbd">s</span>
          </Button>
          <a className={buttonVariants()} href={job.applyUrl} target="_blank" rel="noopener noreferrer">
            Apply on {applyLabel} <ExternalLink size={14} aria-hidden="true" />
          </a>
          <span className="help">Tailoring, cover letters and the tracker are coming soon.</span>
        </div>

        <div className="sec">
          <h3 className="label-k">Fit</h3>
          {score ? (
            <>
              <div className="fit">
                <span className="big">{score.fitScore}</span>
                <span className="font-mono text-muted">/ 10</span>
                <span className={cn('chip', confidenceTone[score.confidence])}>confidence: {score.confidence}</span>
              </div>
              <ul className="plain">
                {score.reasons.map((r) => (
                  <li key={r}>{r}</li>
                ))}
              </ul>
            </>
          ) : (
            <p className="help">
              Not scored. This job was ranked by title and skills only, because AI scoring was unavailable for that run
              or your Discovery budget was used up.
            </p>
          )}
        </div>

        {(matched.length > 0 || missing.length > 0 || required.length > 0) && (
          <div className="sec">
            <h3 className="label-k">Skills vs your résumé</h3>
            {matched.map((s) => (
              <div className="req" key={`m-${s}`}>
                <span className="chip ok">met</span>
                <span>{s}</span>
              </div>
            ))}
            {missing.map((s) => (
              <div className="req" key={`x-${s}`}>
                <span className="chip bad">missing</span>
                <span>{s}</span>
              </div>
            ))}
            {required.map((s) => (
              <div className="req" key={`r-${s}`}>
                <span className="chip">required</span>
                <span>{s}</span>
              </div>
            ))}
          </div>
        )}

        <div className="sec">
          <h3 className="label-k">Eligibility</h3>
          <div>
            <EligibilityChip eligibility={job.eligibility} />
          </div>
          {job.eligibilityEvidence && <div className="quote">“{job.eligibilityEvidence}”</div>}
          {job.eligibility === 'unclear' && (
            <div className="alert warn" style={{ marginTop: 10 }}>
              <TriangleAlert size={16} aria-hidden="true" />
              <span>The listing does not say whether candidates in India are accepted. Check before applying.</span>
            </div>
          )}
          {job.timezoneRequirement && <p className="help mt-2">Working hours: {job.timezoneRequirement}</p>}
        </div>

        {(job.summary || job.description || detail.isPending) && (
          <div className="sec">
            <h3 className="label-k">About the role</h3>
            {job.summary && <p className="mb-2">{job.summary}</p>}
            {detail.isPending ? (
              <Skeleton lines={3} />
            ) : job.description ? (
              <details>
                <summary className="cursor-pointer text-accent">Full description</summary>
                <div className="desc mt-2">{job.description}</div>
              </details>
            ) : null}
            {job.descriptionCompleteness === 'limited' && (
              <p className="help mt-2">The source shows only part of this description.</p>
            )}
          </div>
        )}

        <div className="sec">
          <h3 className="label-k">Details</h3>
          <div className="grid3 kv">
            <div>
              <div className="kk">Compensation</div>
              <div className="vv">
                {job.salaryLabel === 'not_listed' ? (
                  'Not listed'
                ) : (
                  <>
                    {salaryShort(job)} <span className="text-muted">({job.salaryLabel})</span>
                    {approxInr(job.salary) && <div className="help">{approxInr(job.salary)}</div>}
                  </>
                )}
              </div>
            </div>
            <div>
              <div className="kk">Source</div>
              <div className="vv">
                {/* A normal, followed link to the listing: sources require it (PRD F14). */}
                <a href={job.source.sourceUrl} target="_blank" rel="noopener">
                  {job.source.attributionText ?? `Source: ${job.source.name}`}{' '}
                  <ExternalLink size={12} aria-hidden="true" />
                </a>
                {job.alsoOn.length > 0 && (
                  <div className="help">
                    Also on:{' '}
                    {job.alsoOn.map((a, i) => (
                      <span key={a.sourceKey}>
                        {i > 0 && ', '}
                        <a href={a.sourceUrl} target="_blank" rel="noopener">
                          {a.sourceName}
                        </a>
                      </span>
                    ))}
                  </div>
                )}
              </div>
            </div>
            <div>
              <div className="kk">Posted</div>
              <div className="vv">{ago(job.postedAt)}</div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

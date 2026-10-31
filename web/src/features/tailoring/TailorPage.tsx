import { ArrowLeft } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import { isApiError } from '@/api/client';
import { useConfirmedSkills, useJob, useMeta } from '@/api/queries';
import {
  downloadUrl,
  isBusy,
  useAnalysis,
  useFinalize,
  useTailoredDocuments,
  useUserJob,
  type TailoredDocument,
} from '@/api/tailoring';
import { LoadError, PageLoading, StatusPill } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button, buttonVariants } from '@/components/ui/button';
import { EligibilityChip } from '@/features/shortlist/EligibilityChip';
import { cn } from '@/lib/utils';
import { CoverLetterTab } from './CoverLetterTab';
import { ResumeTab } from './ResumeTab';
import { errorText } from './messages';
import { PasteDescription } from './shared';

type Tab = 'resume' | 'cover' | 'prep';

const tabs: [Tab, string][] = [
  ['resume', 'Résumé changes'],
  ['cover', 'Cover letter'],
  ['prep', 'Interview prep'],
];

/**
 * The tailoring workspace for one tracked job (design/tailorly-app.html #tailor): ATS match before → after, the stepper
 * Analysis → Confirm skills → Review changes → Download, and tabs for résumé changes, the cover letter and (M5) interview
 * prep. The résumé tab ends in the sticky action bar that finalises and downloads.
 */
export function TailorPage() {
  const { userJobId = '' } = useParams();
  const [params, setParams] = useSearchParams();
  const tab: Tab = params.get('tab') === 'cover' ? 'cover' : params.get('tab') === 'prep' ? 'prep' : 'resume';
  const [startedAt, setStartedAt] = useState(() => Date.now());
  const restart = () => setStartedAt(Date.now());

  const userJob = useUserJob(userJobId);
  const job = useJob(userJob.data?.jobId ?? null);
  const analysis = useAnalysis(userJobId, startedAt);
  const docs = useTailoredDocuments(userJobId, startedAt);
  const confirmed = useConfirmedSkills();

  if (userJob.isPending) return <PageLoading />;
  if (userJob.error || !userJob.data)
    return (
      <div className="page narrow">
        {isApiError(userJob.error) && userJob.error.status === 404 ? (
          <p className="empty">This job is not on your list.</p>
        ) : (
          <LoadError error={userJob.error} onRetry={() => void userJob.refetch()} />
        )}
      </div>
    );

  const uj = userJob.data;
  const resumeDoc = docs.data?.find((d) => d.kind === 'resume') ?? null;
  const letter = docs.data?.find((d) => d.kind === 'cover_letter') ?? null;
  const a = analysis.data;
  const before = resumeDoc?.atsBefore ?? a?.atsScore ?? null;
  const after =
    resumeDoc && resumeDoc.status !== 'queued' && resumeDoc.status !== 'running' ? resumeDoc.atsAfter : null;
  const final = resumeDoc?.status === 'ready';
  const needsDescription =
    !uj.hasUserDescription && (!uj.descriptionAvailable || job.data?.descriptionCompleteness === 'limited');

  const analysed = a?.status === 'ready';
  const reviewing =
    !!resumeDoc && ['proposed', 'finalizing', 'failed'].includes(resumeDoc.status) && resumeDoc.edits.length > 0;
  const steps: [string, 'done' | 'now' | ''][] = [
    ['Analysis', analysed ? 'done' : 'now'],
    ['Confirm skills', !analysed ? '' : resumeDoc ? 'done' : 'now'],
    ['Review changes', final ? 'done' : reviewing ? 'now' : ''],
    ['Download', final ? 'now' : ''],
  ];

  return (
    <>
      <div className="page" style={{ paddingBottom: 24 }}>
        <Link
          to={uj.jobId ? `/shortlist?job=${uj.jobId}` : '/shortlist'}
          className="row"
          style={{ marginBottom: 10, width: 'fit-content' }}
        >
          <ArrowLeft size={16} aria-hidden="true" /> Back to shortlist
        </Link>
        <div className="page-title">
          <div>
            <h1>{uj.title}</h1>
            <div className="sub">
              {uj.companyName}
              {job.data?.locationText ? ` · ${job.data.locationText}` : ''}{' '}
              {job.data && <EligibilityChip eligibility={job.data.eligibility} />}
            </div>
          </div>
          <div className="card flat" style={{ padding: '10px 14px' }}>
            <div className="help">ATS match (estimated)</div>
            <div className="font-mono" style={{ fontSize: 22, fontWeight: 600 }}>
              {before ?? '—'}
              {after !== null && (
                <>
                  {' → '}
                  <span style={{ color: 'var(--accent)' }}>{after}</span>
                </>
              )}
            </div>
            <div className="help">
              {after === null ? 'Before tailoring' : final ? 'Final' : 'Projected'} · real ATS systems vary
            </div>
          </div>
        </div>

        <div className="stepper" aria-label="Progress">
          {steps.map(([label, state], i) => (
            <div key={label} className={cn('step', state)} aria-current={state === 'now' ? 'step' : undefined}>
              <span className="n">{state === 'done' ? '✓' : i + 1}</span>
              {label}
            </div>
          ))}
        </div>

        <div className="tabs" role="tablist">
          {tabs.map(([value, label]) => (
            <button
              key={value}
              role="tab"
              type="button"
              aria-selected={tab === value}
              onClick={() => setParams(value === 'resume' ? {} : { tab: value }, { replace: true })}
            >
              {label}
            </button>
          ))}
        </div>

        {(analysis.error && !isApiError(analysis.error)) || docs.error ? (
          <LoadError
            error={analysis.error ?? docs.error}
            onRetry={() => {
              void analysis.refetch();
              void docs.refetch();
            }}
          />
        ) : tab === 'resume' ? (
          <ResumeTab
            userJob={uj}
            needsDescription={needsDescription}
            analysis={a}
            analysisError={analysis.error}
            doc={resumeDoc}
            confirmed={confirmed.data?.map((s) => s.skill) ?? []}
            onStarted={restart}
          />
        ) : tab === 'cover' ? (
          needsDescription && !letter ? (
            <PasteDescription userJobId={uj.id} onSaved={() => void userJob.refetch()} />
          ) : (
            <CoverLetterTab key={letter?.id ?? 'none'} userJobId={uj.id} letter={letter} onStarted={restart} />
          )
        ) : (
          <div className="card">
            <h2>Interview prep</h2>
            <div className="empty">Interview prep arrives with the tracker. It will use only your real experience.</div>
          </div>
        )}
      </div>
      {tab === 'resume' && resumeDoc && (resumeDoc.edits.length > 0 || resumeDoc.status === 'ready') && (
        <FinalizeBar userJobId={uj.id} doc={resumeDoc} onStarted={restart} />
      )}
    </>
  );
}

/**
 * Prototype action bar: decision counts, the finalising state, and either Finalise (applies the accepted changes to a
 * copy of your file and re-scores) or the downloads once ready. PDF only when the PDF export flag is on.
 */
function FinalizeBar({
  userJobId,
  doc,
  onStarted,
}: {
  userJobId: string;
  doc: TailoredDocument;
  onStarted: () => void;
}) {
  const say = useToast();
  const meta = useMeta();
  const finalize = useFinalize(userJobId, doc.id);
  const count = (d: string) => doc.edits.filter((e) => e.decision === d).length;
  const busy = isBusy(doc.status) || finalize.isPending;
  const ready = doc.status === 'ready';

  // When finalising ends: announce the score and start the download (the button says "Finalize and download").
  const wasFinalizing = useRef(doc.status === 'finalizing');
  useEffect(() => {
    if (wasFinalizing.current && doc.status === 'ready') {
      say(
        `Résumé ready. ATS match re-scored to ${doc.atsAfter ?? '—'}${doc.atsAfterBasis === 'projected' ? ' (projected)' : ''}.`,
      );
      window.location.assign(downloadUrl(doc.id));
    }
    wasFinalizing.current = doc.status === 'finalizing';
  }, [doc.status, doc.atsAfter, doc.atsAfterBasis, doc.id, say]);

  return (
    <div className="actionbar">
      <div className="row">
        <span>
          <b>{count('accepted')}</b> accepted
        </span>
        <span className="text-muted">·</span>
        <span>
          <b>{count('rejected')}</b> rejected
        </span>
        <span className="text-muted">·</span>
        <span>
          <b>{count('pending')}</b> pending
        </span>
        {doc.status === 'finalizing' && <StatusPill status="finalizing" />}
        {finalize.error && <span className="err">{errorText(finalize.error)}</span>}
      </div>
      <div className="row">
        {ready ? (
          <>
            <span className="chip ok">final ATS {doc.atsAfter ?? '—'}</span>
            {doc.formats.includes('pdf') && (
              <a className={buttonVariants()} href={downloadUrl(doc.id, 'pdf')} download>
                Download PDF
              </a>
            )}
            <a className={buttonVariants({ variant: 'primary' })} href={downloadUrl(doc.id)} download>
              Download .docx
            </a>
          </>
        ) : (
          <>
            <Button
              disabled
              title={
                meta.data?.pdfExport
                  ? 'Finalise first, then download as PDF'
                  : 'PDF export is turned off (feature flag)'
              }
            >
              Download PDF
            </Button>
            <Button
              variant="primary"
              disabled={count('accepted') === 0 || busy || (doc.status !== 'proposed' && doc.status !== 'failed')}
              onClick={() => finalize.mutate(undefined, { onSuccess: onStarted })}
            >
              Finalize and download .docx
            </Button>
          </>
        )}
      </div>
    </div>
  );
}

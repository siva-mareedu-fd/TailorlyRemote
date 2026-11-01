import { useId, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { isApiError } from '@/api/client';
import { isBusy, usePasteDescription } from '@/api/tailoring';
import { useAddByoJob, useUserJobPolling } from '@/api/tracker';
import { Alert, Skeleton, StatusPill } from '@/components/Feedback';
import { Button, buttonVariants } from '@/components/ui/button';
import { EligibilityChip } from '@/features/shortlist/EligibilityChip';
import { errorText, failureText } from '@/features/tailoring/messages';
import { cn } from '@/lib/utils';

const MAX = 30_000;
/** Client-side hint only (prototype): a short paste gives a poor score. The server requires non-empty text. */
const MIN = 80;
const nf = new Intl.NumberFormat('en-US');

/**
 * "Add a job" (design/tailorly-app.html #addjob, PRD F12, Journey C): paste a job found on LinkedIn, Naukri or a company
 * site, with an optional title, company and link. The link is saved for the user's reference and never opened by us.
 * The job is enriched and scored in the background (queued → running → ready | failed); the result shows the fit,
 * eligibility and reasons, then leads into tailoring or the tracker.
 */
export function AddJobPage() {
  const navigate = useNavigate();
  const ids = { desc: useId(), title: useId(), company: useId(), url: useId(), help: useId(), err: useId() };
  const [desc, setDesc] = useState('');
  const [title, setTitle] = useState('');
  const [company, setCompany] = useState('');
  const [url, setUrl] = useState('');
  const [invalid, setInvalid] = useState<string | null>(null);
  const [added, setAdded] = useState<{ id: string; startedAt: number } | null>(null);
  const add = useAddByoJob();
  const entry = useUserJobPolling(added?.id ?? null, added?.startedAt ?? 0);
  const retry = usePasteDescription(added?.id ?? '');
  const uj = entry.data;
  const busy = add.isPending || (!!added && (!uj || isBusy(uj.enrichmentStatus)));

  const submit = () => {
    const text = desc.trim();
    if (text.length < MIN) {
      setInvalid(`Paste the full job description (at least ${MIN} characters) so the score is accurate.`);
      return;
    }
    if (url.trim() && !/^https?:\/\//i.test(url.trim())) {
      setInvalid('The link must start with http:// or https://.');
      return;
    }
    add.mutate(
      { description: text, url: url.trim() || null, title: title.trim() || null, companyName: company.trim() || null },
      { onSuccess: (r) => setAdded({ id: r.id, startedAt: Date.now() }) },
    );
  };

  const fieldErrors =
    isApiError(add.error) && Object.keys(add.error.fieldErrors).length > 0 ? add.error.fieldErrors : undefined;

  return (
    <div className="page narrow">
      <div className="page-title">
        <div>
          <h1>Add a job</h1>
          <div className="sub">
            Found a role on LinkedIn, Naukri or a company site? Paste it here and get the same score, analysis and
            tailoring.
          </div>
        </div>
      </div>

      <div className="card">
        <div className="field">
          <label htmlFor={ids.desc}>
            Job description <span className="text-muted">(required)</span>
          </label>
          <textarea
            id={ids.desc}
            className="textarea"
            style={{ minHeight: 220 }}
            maxLength={MAX}
            placeholder="Paste the full job description here"
            aria-describedby={`${ids.help} ${ids.err}`}
            aria-invalid={invalid ? true : undefined}
            value={desc}
            onChange={(e) => {
              setDesc(e.target.value);
              setInvalid(null);
            }}
          />
          <div className="row between">
            <span className="help" id={ids.help}>
              Up to 30,000 characters.
            </span>
            <span className="help font-mono">
              {nf.format(desc.length)} / {nf.format(MAX)}
            </span>
          </div>
          <div className="err" id={ids.err} role="alert">
            {invalid ?? fieldErrors?.description?.[0] ?? ''}
          </div>
        </div>
        <div className="grid2">
          <div className="field">
            <label htmlFor={ids.title}>Title</label>
            <input
              id={ids.title}
              className="input"
              maxLength={200}
              placeholder="Optional"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
            />
          </div>
          <div className="field">
            <label htmlFor={ids.company}>Company</label>
            <input
              id={ids.company}
              className="input"
              maxLength={200}
              placeholder="Optional"
              value={company}
              onChange={(e) => setCompany(e.target.value)}
            />
          </div>
        </div>
        <div className="field">
          <label htmlFor={ids.url}>Link</label>
          <input
            id={ids.url}
            className="input"
            inputMode="url"
            maxLength={2000}
            placeholder="https://… (optional)"
            value={url}
            onChange={(e) => {
              setUrl(e.target.value);
              setInvalid(null);
            }}
          />
          <span className="help">Saved for your reference only. We never open this link.</span>
          {fieldErrors?.url && <div className="err">{fieldErrors.url[0]}</div>}
        </div>
        {add.error && !fieldErrors && (
          <Alert tone="bad" className="mb-3">
            {errorText(add.error)}
          </Alert>
        )}
        <div className="row">
          <Button variant="primary" disabled={busy} onClick={submit}>
            Add and score
          </Button>
          {added && uj && isBusy(uj.enrichmentStatus) && <StatusPill status={uj.enrichmentStatus!} />}
          <span className="help">Uses about 3k tokens of Documents</span>
        </div>
        {busy && added && (
          <div style={{ marginTop: 14 }}>
            <Skeleton lines={4} />
          </div>
        )}
      </div>

      {uj && uj.enrichmentStatus === 'failed' && (
        <Alert tone="bad" className="mt-[14px]">
          <b>We could not read this job.</b> {failureText(uj.enrichmentErrorCode)}{' '}
          <Button
            size="sm"
            disabled={retry.isPending}
            onClick={() =>
              retry.mutate(desc.trim(), { onSuccess: () => setAdded({ id: uj.id, startedAt: Date.now() }) })
            }
          >
            Try again
          </Button>
        </Alert>
      )}

      {uj && uj.enrichmentStatus === 'ready' && (
        <div className="card" style={{ marginTop: 14 }}>
          <div className="row between">
            <div>
              <h2>{uj.title}</h2>
              <div className="sub">
                {uj.companyName}
                {uj.assessment?.workMode ? ` · ${uj.assessment.workMode}` : ''}
              </div>
            </div>
            {uj.fitScore !== null && (
              <div className={cn('sc', uj.fitScore >= 9 && 'hi')} style={{ fontSize: 26 }}>
                {uj.fitScore}
                <span className="text-muted" style={{ fontSize: 13 }}>
                  /10
                </span>
              </div>
            )}
          </div>
          <div className="row" style={{ margin: '10px 0 14px', gap: 6 }}>
            {uj.assessment && <EligibilityChip eligibility={uj.assessment.eligibility} />}
            <span className="chip">source: added by you</span>
          </div>
          <ul className="plain">
            {uj.assessment?.fitReasons.map((r, i) => (
              <li key={i}>{r}</li>
            ))}
            {uj.assessment?.eligibilityEvidence && <li>Eligibility evidence: “{uj.assessment.eligibilityEvidence}”</li>}
          </ul>
          {uj.fitScore === null && (
            <p className="help">
              No fit score yet: it needs a ready résumé and Discovery budget. Analysis and tailoring still work.
            </p>
          )}
          <div className="row" style={{ marginTop: 14 }}>
            <Button variant="primary" onClick={() => navigate(`/tailor/${uj.id}`)}>
              Tailor résumé
            </Button>
            <Link className={buttonVariants()} to="/tracker">
              View in tracker
            </Link>
          </div>
        </div>
      )}
    </div>
  );
}

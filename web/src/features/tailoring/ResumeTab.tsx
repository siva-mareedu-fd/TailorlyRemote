import { useState } from 'react';
import { isApiError } from '@/api/client';
import {
  isBusy,
  useConfirmSkill,
  useDecide,
  useStartAnalysis,
  useStartTailoring,
  type Analysis,
  type Decision,
  type TailoredDocument,
  type TailoredEdit,
  type UserJob,
} from '@/api/tailoring';
import { Alert, Skeleton, StatusPill } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { DiffSide } from './Diff';
import { errorText, failureText } from './messages';
import { AtsBreakdown, ConfirmSkills, PasteDescription } from './shared';

const flagChip: Record<string, [string, string]> = {
  new_fact: ['warn', 'not in your résumé'],
  length: ['warn', 'too long'],
  formatting_simplified: ['info', 'formatting simplified'],
};

const sectionLabel = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);
const key = (s: string) => s.trim().toLowerCase();

/**
 * The Résumé changes tab of the tailoring workspace (design/tailorly-app.html #tailor): analysis → confirm skills →
 * proposed edits with a word diff, flags and accept/reject → finalise (in the action bar). Every AI step shows queued,
 * running, ready and failed.
 */
export function ResumeTab({
  userJob,
  needsDescription,
  analysis,
  analysisError,
  doc,
  confirmed,
  onStarted,
}: {
  userJob: UserJob;
  needsDescription: boolean;
  analysis: Analysis | null | undefined;
  analysisError: unknown;
  doc: TailoredDocument | null;
  confirmed: string[];
  onStarted: () => void;
}) {
  const say = useToast();
  const [declined, setDeclined] = useState<string[]>([]);
  const [pasting, setPasting] = useState(false);
  const startAnalysis = useStartAnalysis(userJob.id);
  const startTailoring = useStartTailoring(userJob.id);
  const confirm = useConfirmSkill(userJob.id);
  const decide = useDecide(userJob.id, doc?.id ?? '');
  const confirmedKeys = new Set(confirmed.map(key));

  const analyse = () =>
    startAnalysis.mutate(undefined, {
      onSuccess: onStarted,
      onError: (e) => isApiError(e, 'JOB_DESCRIPTION_REQUIRED') && setPasting(true),
    });
  const tailor = () => startTailoring.mutate(undefined, { onSuccess: onStarted });

  // Skills to confirm: the analysis gaps and the terms proposed edits would add, minus those already confirmed.
  const toConfirm = new Map<string, { skill: string; likelyHas: boolean }>();
  for (const g of analysis?.gaps ?? [])
    if (!g.confirmed) toConfirm.set(key(g.skill), { skill: g.skill, likelyHas: g.likelyHas });
  for (const e of doc?.edits ?? [])
    for (const t of e.newTerms)
      if (!confirmedKeys.has(key(t)) && !toConfirm.has(key(t))) toConfirm.set(key(t), { skill: t, likelyHas: false });

  const onConfirm = (skill: string) =>
    confirm.mutate(skill, {
      onSuccess: () => {
        say(`${skill} saved to your confirmed skills`);
        // As in the prototype: edits held back only by this skill go back to pending for review.
        if (doc?.status !== 'proposed') return;
        const nowConfirmed = new Set([...confirmedKeys, key(skill)]);
        const reopen = doc.edits.filter(
          (e) =>
            e.decision === 'rejected' && e.newTerms.length > 0 && e.newTerms.every((t) => nowConfirmed.has(key(t))),
        );
        if (reopen.length > 0) decide.mutate(reopen.map((e) => ({ editId: e.id, decision: 'pending' as Decision })));
      },
      onError: (e) => say(errorText(e)),
    });

  const skillCard = (
    <ConfirmSkills
      skills={[...toConfirm.values()]}
      declined={declined}
      confirming={confirm.isPending ? (confirm.variables ?? '') : null}
      onConfirm={onConfirm}
      onDecline={(s) => setDeclined((d) => [...d, s])}
    />
  );

  // ---------- before an analysis ----------
  if (analysis === undefined && !analysisError) return <Skeleton lines={5} />;
  if ((needsDescription && !analysis) || pasting)
    return (
      <PasteDescription
        userJobId={userJob.id}
        onSaved={() => {
          setPasting(false);
          say('Description saved. Running the analysis…');
          analyse();
        }}
      />
    );
  if (!analysis)
    return (
      <div className="card">
        <h2>Analyse this job</h2>
        <p className="sub">
          We compare your résumé with what this job asks for: keywords, requirements, title and seniority, relevance of
          your experience and format. Then we suggest changes you approve one by one.
        </p>
        {startAnalysis.error && !isApiError(startAnalysis.error, 'JOB_DESCRIPTION_REQUIRED') && (
          <Alert tone="bad" className="mb-3">
            {errorText(startAnalysis.error)}
          </Alert>
        )}
        <div className="row">
          <Button variant="primary" disabled={startAnalysis.isPending} onClick={analyse}>
            Run ATS analysis
          </Button>
          <span className="help">Uses about 5k tokens of your Documents budget</span>
        </div>
      </div>
    );
  if (isBusy(analysis.status))
    return (
      <div className="card">
        <div className="row" style={{ marginBottom: 12 }}>
          <StatusPill status={analysis.status} />
          <span className="help">Analysing your résumé against this job…</span>
        </div>
        <Skeleton lines={4} />
      </div>
    );
  if (analysis.status === 'failed')
    return (
      <Alert
        tone="bad"
        action={
          <Button size="sm" disabled={startAnalysis.isPending} onClick={analyse}>
            Try again
          </Button>
        }
      >
        <b>The analysis failed.</b> {failureText(analysis.errorCode)}
      </Alert>
    );

  // ---------- analysed: no proposal yet, or it is being made ----------
  if (
    !doc ||
    (doc.status === 'failed' && doc.edits.length === 0) ||
    (isBusy(doc.status) && doc.status !== 'finalizing')
  ) {
    const busy = !!doc && isBusy(doc.status);
    return (
      <>
        {skillCard}
        <div
          className="grid2"
          style={{ gridTemplateColumns: 'minmax(0,1fr) minmax(260px,360px)', alignItems: 'start' }}
        >
          <AnalysisSummary analysis={analysis} />
          <div className="stack">
            <AtsBreakdown before={analysis.components} after={null} final={false} />
            <div className="card">
              <h2>Tailor your résumé</h2>
              <p className="sub">
                We suggest small edits to paragraphs you are allowed to change. Names, employers, titles, dates and
                education stay locked, and nothing you have not stated is added.
              </p>
              {doc?.status === 'failed' && (
                <Alert tone="bad" className="mb-3">
                  {failureText(doc.errorCode)}
                </Alert>
              )}
              {startTailoring.error && (
                <Alert tone="bad" className="mb-3">
                  {errorText(startTailoring.error)}
                </Alert>
              )}
              {doc && busy ? (
                <>
                  <div className="row" style={{ marginBottom: 12 }}>
                    <StatusPill status={doc.status} />
                    <span className="help">Proposing changes…</span>
                  </div>
                  <Skeleton lines={3} />
                </>
              ) : (
                <div className="row">
                  <Button variant="primary" disabled={startTailoring.isPending} onClick={tailor}>
                    Propose changes
                  </Button>
                  <span className="help">Uses about 7k tokens</span>
                </div>
              )}
            </div>
          </div>
        </div>
      </>
    );
  }

  // ---------- proposed, finalising or finished ----------
  return (
    <>
      {skillCard}
      {doc.status === 'failed' && (
        <Alert tone="bad" className="mb-3.5">
          <b>Finalising failed.</b> {failureText(doc.errorCode)}
        </Alert>
      )}
      <EditsPanel
        doc={doc}
        analysis={analysis}
        confirmedKeys={confirmedKeys}
        onDecide={(d) => decide.mutate(d, { onError: (e) => say(errorText(e)) })}
        onRegenerate={doc.status === 'ready' || doc.status === 'failed' ? tailor : undefined}
        regenerating={startTailoring.isPending}
      />
    </>
  );
}

/** What the analysis found: requirements with their status, missing keywords and format findings. */
function AnalysisSummary({ analysis }: { analysis: Analysis }) {
  const tone: Record<string, string> = { met: 'ok', partial: 'warn', missing: 'bad' };
  return (
    <div className="card">
      <h2>What this job asks for</h2>
      <p className="sub">From the job posting, checked against your résumé.</p>
      {analysis.requirements.map((r) => (
        <div className="req" key={r.text}>
          <span className={cn('chip', tone[r.status])}>{r.status}</span>
          <span>
            {r.text} {r.type === 'preferred' && <span className="help">(preferred)</span>}
          </span>
        </div>
      ))}
      {(analysis.keywords?.missing.length ?? 0) > 0 && (
        <div className="mt-3">
          <h3 className="label-k">Missing keywords</h3>
          <div className="row" style={{ gap: 6 }}>
            {analysis.keywords!.missing.map((k) => (
              <span className="chip" key={k}>
                {k}
              </span>
            ))}
          </div>
        </div>
      )}
      {analysis.findings.length > 0 && (
        <div className="mt-3">
          <h3 className="label-k">Format</h3>
          <div className="stack">
            {analysis.findings.map((f) => (
              <Alert tone={f.severity === 'warn' ? 'warn' : 'info'} key={f.code}>
                {f.message} {f.suggestion && <span className="text-muted">{f.suggestion}</span>}
              </Alert>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/** Prototype `editsPanel`: the edit list, the selected edit's diff and decision, and the ATS breakdown. */
function EditsPanel({
  doc,
  analysis,
  confirmedKeys,
  onDecide,
  onRegenerate,
  regenerating,
}: {
  doc: TailoredDocument;
  analysis: Analysis;
  confirmedKeys: Set<string>;
  onDecide: (d: { editId: string; decision: Decision }[]) => void;
  onRegenerate?: () => void;
  regenerating: boolean;
}) {
  const say = useToast();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const editable = doc.status === 'proposed';
  const e = doc.edits.find((x) => x.id === selectedId) ?? doc.edits[0];
  const unconfirmed = (x: TailoredEdit) => x.newTerms.filter((t) => !confirmedKeys.has(key(t)));

  if (doc.edits.length === 0)
    return (
      <div className="card">
        <h2>No changes proposed</h2>
        <p className="sub">
          Your résumé already covers what we could improve truthfully for this job.
          {doc.unaddressedGaps.length > 0 && <> Not addressed: {doc.unaddressedGaps.join(', ')}.</>}
        </p>
        {onRegenerate && (
          <Button disabled={regenerating} onClick={onRegenerate}>
            Try again
          </Button>
        )}
      </div>
    );

  const acceptSafe = () => {
    const safe = doc.edits.filter(
      (x) => !x.flags.includes('new_fact') && !x.flags.includes('length') && x.decision !== 'accepted',
    );
    if (safe.length === 0) return say('No more safe changes to accept. Review the flagged ones yourself.');
    onDecide(safe.map((x) => ({ editId: x.id, decision: 'accepted' })));
    say(`${safe.length} safe change${safe.length === 1 ? '' : 's'} accepted. Review flagged ones yourself.`);
  };

  const blocked = unconfirmed(e);
  return (
    <div className="tw">
      <div>
        <div className="row between" style={{ marginBottom: 8 }}>
          <h2 className="m-0">
            Proposed changes{' '}
            <span className="text-muted font-mono" style={{ fontSize: 12 }}>
              {doc.edits.length}
            </span>
          </h2>
          {editable ? (
            <Button size="sm" onClick={acceptSafe}>
              Accept all safe
            </Button>
          ) : (
            onRegenerate && (
              <Button
                size="sm"
                variant="ghost"
                disabled={regenerating}
                onClick={onRegenerate}
                title="Uses about 7k tokens"
              >
                Start over
              </Button>
            )
          )}
        </div>
        <div className="editlist" role="listbox" aria-label="Proposed changes">
          {doc.edits.map((x) => (
            <button
              key={x.id}
              type="button"
              role="option"
              className="edit"
              aria-selected={x.id === e.id}
              onClick={() => setSelectedId(x.id)}
            >
              <span className={cn('dot', x.decision !== 'pending' && x.decision)} aria-label={x.decision}>
                {x.decision === 'accepted' ? '✓' : x.decision === 'rejected' ? '–' : ''}
              </span>
              <span>
                <div className="es">{sectionLabel(x.section)}</div>
                <div className="ep">{x.proposed}</div>
                {x.flags.length > 0 && (
                  <div className="row" style={{ marginTop: 6, gap: 5 }}>
                    {x.flags.map((f) => (
                      <span key={f} className={cn('chip', flagChip[f]?.[0])}>
                        {flagChip[f]?.[1] ?? f}
                      </span>
                    ))}
                  </div>
                )}
              </span>
            </button>
          ))}
        </div>
      </div>

      <div className="stack">
        <div className="card">
          <div className="row between" style={{ marginBottom: 12 }}>
            <div className="row" style={{ gap: 6 }}>
              <span className="chip">{sectionLabel(e.section)}</span>
              {e.flags.map((f) => (
                <span key={f} className={cn('chip', flagChip[f]?.[0])}>
                  {flagChip[f]?.[1] ?? f}
                </span>
              ))}
            </div>
            <span className="font-mono text-muted" style={{ fontSize: 12 }}>
              {e.paragraphId} · {e.decision}
            </span>
          </div>
          <div className="diffbox">
            <div className="lab">Original</div>
            <div className="txt">
              <DiffSide diff={e.diff} side="original" />
            </div>
            <div className="lab">Proposed</div>
            <div className="txt">
              <DiffSide diff={e.diff} side="proposed" />
            </div>
          </div>
          <p className="sub" style={{ margin: '12px 0' }}>
            <b>Why:</b> {e.rationale}
          </p>
          <div className="stack" style={{ marginBottom: 12 }}>
            {e.flags.includes('new_fact') && (
              <Alert tone="warn">
                {blocked.length > 0 ? (
                  <>
                    This change adds <b>{blocked.join(', ')}</b>, which is not in your résumé. Confirm it above to
                    enable the change.
                  </>
                ) : (
                  <>This change adds {e.newTerms.join(', ')}, which you confirmed. Review the wording.</>
                )}
              </Alert>
            )}
            {e.flags.includes('formatting_simplified') && (
              <Alert tone="info">
                Part of this line has its own formatting (for example a bold word). Accepting applies the line’s main
                style to the whole line.
              </Alert>
            )}
            {e.flags.includes('length') && (
              <Alert tone="warn">
                This change is noticeably longer than the original and may push your résumé onto another page. Consider
                rejecting it.
              </Alert>
            )}
          </div>
          {editable && (
            <div className="row">
              <Button
                variant="primary"
                disabled={blocked.length > 0}
                title={blocked.length > 0 ? 'Confirm the skill first' : undefined}
                onClick={() => onDecide([{ editId: e.id, decision: 'accepted' }])}
              >
                Accept
              </Button>
              <Button onClick={() => onDecide([{ editId: e.id, decision: 'rejected' }])}>Reject</Button>
              <Button variant="ghost" onClick={() => onDecide([{ editId: e.id, decision: 'pending' }])}>
                Reset
              </Button>
            </div>
          )}
        </div>
        <AtsBreakdown before={analysis.components} after={doc.atsBreakdown} final={doc.atsAfterBasis === 'rescored'} />
        {doc.unaddressedGaps.length > 0 && (
          <p className="help">
            Not addressed, because your résumé does not show them: {doc.unaddressedGaps.join(', ')}.
          </p>
        )}
      </div>
    </div>
  );
}

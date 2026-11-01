import { useState } from 'react';
import { isApiError } from '@/api/client';
import { useInterviewPrep, useStartInterviewPrep } from '@/api/tracker';
import { isBusy } from '@/api/tailoring';
import { Alert, LoadError, Skeleton, StatusPill } from '@/components/Feedback';
import { Button } from '@/components/ui/button';
import { errorText, failureText } from './messages';
import { PasteDescription } from './shared';

/**
 * The Interview prep tab (prototype `prepPanel`): likely technical topics, behavioural questions with talking points
 * from the résumé only, gaps to prepare and questions to ask. Queued → running → ready (Regenerate) or failed.
 */
export function InterviewPrepTab({ userJobId, onPasted }: { userJobId: string; onPasted: () => void }) {
  const [startedAt, setStartedAt] = useState(() => Date.now());
  const [pasting, setPasting] = useState(false);
  const prep = useInterviewPrep(userJobId, startedAt);
  const start = useStartInterviewPrep(userJobId);
  const p = prep.data;
  const busy = isBusy(p?.status) || start.isPending;

  const generate = () =>
    start.mutate(undefined, {
      onSuccess: () => setStartedAt(Date.now()),
      onError: (e) => isApiError(e, 'JOB_DESCRIPTION_REQUIRED') && setPasting(true),
    });

  if (pasting)
    return (
      <PasteDescription
        userJobId={userJobId}
        onSaved={() => {
          setPasting(false);
          onPasted();
          generate();
        }}
      />
    );

  let out;
  if (prep.error) out = <LoadError error={prep.error} onRetry={() => void prep.refetch()} />;
  else if (prep.isPending) out = <Skeleton lines={5} />;
  else if (!p) out = <div className="empty">Generate prep notes from this job and your résumé.</div>;
  else if (isBusy(p.status))
    out = (
      <>
        <div className="row" style={{ marginBottom: 12 }}>
          <StatusPill status={p.status} />
          <span className="help">Preparing your notes…</span>
        </div>
        <Skeleton lines={6} />
      </>
    );
  else if (p.status === 'failed' || !p.content)
    out = (
      <Alert tone="bad">
        <b>The prep notes could not be written.</b> {failureText(p.errorCode)}
      </Alert>
    );
  else
    out = (
      <div className="stack">
        <div>
          <h3 className="k">Technical topics</h3>
          <ul className="plain">
            {p.content.technicalTopics.map((t, i) => (
              <li key={i}>
                <span>
                  <b>{t.topic}.</b> <span className="text-muted">{t.why}</span>
                </span>
              </li>
            ))}
          </ul>
        </div>
        <div>
          <h3 className="k">Behavioural questions</h3>
          <div className="stack">
            {p.content.behavioural.map((b, i) => (
              <div key={i}>
                <b>{b.question}</b>
                <div className="help">From your résumé: {b.talkingPointsFromResume.join(' · ')}</div>
              </div>
            ))}
          </div>
        </div>
        {p.content.gapsToPrepare.length > 0 && (
          <div>
            <h3 className="k">Gaps to prepare</h3>
            <div className="row" style={{ gap: 6 }}>
              {p.content.gapsToPrepare.map((g) => (
                <span key={g} className="chip bad">
                  {g}
                </span>
              ))}
            </div>
          </div>
        )}
        <div>
          <h3 className="k">Questions to ask them</h3>
          <ul className="plain">
            {p.content.questionsToAsk.map((q, i) => (
              <li key={i}>{q}</li>
            ))}
          </ul>
        </div>
      </div>
    );

  return (
    <div className="card">
      <div className="row between" style={{ marginBottom: 14 }}>
        <div>
          <h2>Interview prep</h2>
          <p className="sub" style={{ margin: 0 }}>
            Talking points use only your real experience.
          </p>
        </div>
        <Button variant="primary" disabled={busy} onClick={generate}>
          {p?.status === 'ready' ? 'Regenerate' : 'Generate prep'}
        </Button>
      </div>
      {start.error && !isApiError(start.error, 'JOB_DESCRIPTION_REQUIRED') && (
        <Alert tone="bad" className="mb-3">
          {errorText(start.error)}
        </Alert>
      )}
      {out}
    </div>
  );
}

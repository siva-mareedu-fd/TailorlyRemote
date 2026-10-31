import { useId, useState } from 'react';
import { isApiError } from '@/api/client';
import { useMeta } from '@/api/queries';
import { downloadUrl, isBusy, useSaveLetter, useStartCoverLetter, type TailoredDocument } from '@/api/tailoring';
import { Segmented } from '@/components/Controls';
import { Alert, Skeleton, StatusPill } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button, buttonVariants } from '@/components/ui/button';
import { errorText, failureText } from './messages';
import { PasteDescription } from './shared';

const toneLabel: Record<string, string> = { concise: 'Concise', confident: 'Confident' };
const toneHelp: Record<string, string> = { concise: 'plain and brief', confident: 'achievement-led' };

const words = (s: string) => s.split(/\s+/).filter(Boolean).length;
const paragraphsOf = (text: string) =>
  text
    .split(/\n\s*\n/)
    .map((p) => p.replace(/\s*\n\s*/g, ' ').trim())
    .filter((p) => p.length > 0);

/**
 * The Cover letter tab (prototype `coverPanel`): tone (configured tones with their word limits), optional notes, then
 * the letter: queued → running → ready (editable, saved as plain paragraphs) or failed. Downloads the .docx.
 */
export function CoverLetterTab({
  userJobId,
  letter,
  onStarted,
}: {
  userJobId: string;
  letter: TailoredDocument | null;
  onStarted: () => void;
}) {
  const meta = useMeta();
  const say = useToast();
  const notesId = useId();
  const letterId = useId();
  const tones = meta.data?.tones ?? ['concise', 'confident'];
  const [tone, setTone] = useState(letter?.tone ?? 'concise');
  const [notes, setNotes] = useState(letter?.content?.notes ?? '');
  const [draft, setDraft] = useState<{ id: string; text: string } | null>(null);
  const [pasting, setPasting] = useState(false);
  const start = useStartCoverLetter(userJobId);
  const save = useSaveLetter(userJobId, letter?.id ?? '');

  const busy = isBusy(letter?.status);
  const saved = letter?.content?.paragraphs.join('\n\n') ?? '';
  const text = draft && draft.id === letter?.id ? draft.text : saved;
  const dirty = !!letter && text !== saved;
  const limit = meta.data?.toneWordLimits[tone];

  const generate = () =>
    start.mutate(
      { tone, notes: notes.trim() || null },
      {
        onSuccess: () => {
          setDraft(null);
          onStarted();
        },
        onError: (e) => isApiError(e, 'JOB_DESCRIPTION_REQUIRED') && setPasting(true),
      },
    );

  const saveDraft = (then?: () => void) =>
    save.mutate(paragraphsOf(text), {
      onSuccess: () => {
        setDraft(null);
        say('Cover letter saved');
        then?.();
      },
      onError: (e) => say(errorText(e)),
    });

  if (pasting)
    return (
      <PasteDescription
        userJobId={userJobId}
        onSaved={() => {
          setPasting(false);
          generate();
        }}
      />
    );

  let output;
  if (!letter) output = <div className="empty">No draft yet. Choose a tone and generate one.</div>;
  else if (busy)
    output = (
      <>
        <div className="row" style={{ marginBottom: 12 }}>
          <StatusPill status={letter.status} />
          <span className="help">Writing your letter…</span>
        </div>
        <Skeleton lines={5} />
      </>
    );
  else if (letter.status === 'failed')
    output = (
      <Alert tone="bad">
        <b>The letter could not be written.</b> {failureText(letter.errorCode)}
      </Alert>
    );
  else
    output = (
      <>
        <div className="field">
          <label htmlFor={letterId} className="sr-only">
            Cover letter
          </label>
          <textarea
            id={letterId}
            className="textarea letter"
            style={{ minHeight: 300 }}
            value={text}
            onChange={(e) => setDraft({ id: letter.id, text: e.target.value })}
          />
          <div className="help">
            {words(text)} words · editable{dirty ? ' · unsaved changes' : ''}
          </div>
        </div>
        <div className="row">
          {dirty ? (
            <Button
              variant="primary"
              disabled={save.isPending}
              onClick={() => saveDraft(() => window.location.assign(downloadUrl(letter.id)))}
            >
              Save and download .docx
            </Button>
          ) : (
            <a className={buttonVariants({ variant: 'primary' })} href={downloadUrl(letter.id)} download>
              Download .docx
            </a>
          )}
          {letter.formats.includes('pdf') && !dirty && (
            <a className={buttonVariants()} href={downloadUrl(letter.id, 'pdf')} download>
              Download PDF
            </a>
          )}
          {dirty && (
            <Button disabled={save.isPending} onClick={() => saveDraft()}>
              Save changes
            </Button>
          )}
          <Button disabled={start.isPending} onClick={generate}>
            Regenerate
          </Button>
          <span className="help">Regenerating uses about 3k tokens</span>
        </div>
      </>
    );

  return (
    <div className="grid2" style={{ gridTemplateColumns: 'minmax(260px,320px) minmax(0,1fr)', alignItems: 'start' }}>
      <div className="card">
        <h2>Cover letter</h2>
        <p className="sub">Uses your résumé and this job. No invented facts.</p>
        <div className="field">
          <span className="lbl">Tone</span>
          <Segmented
            label="Tone"
            value={tone}
            options={tones.map((t) => ({ value: t, label: toneLabel[t] ?? t }))}
            onChange={setTone}
          />
          <span className="help">
            {limit ? `Up to about ${limit} words` : ''}
            {toneHelp[tone] ? `, ${toneHelp[tone]}.` : '.'}
          </span>
        </div>
        <div className="field">
          <label htmlFor={notesId}>Notes (optional)</label>
          <input
            id={notesId}
            className="input"
            maxLength={500}
            placeholder="For example: referred by Priya"
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
          />
        </div>
        {start.error && !isApiError(start.error, 'JOB_DESCRIPTION_REQUIRED') && (
          <Alert tone="bad" className="mb-3">
            {errorText(start.error)}
          </Alert>
        )}
        <Button variant="primary" disabled={busy || start.isPending} onClick={generate}>
          Generate cover letter
        </Button>
      </div>
      <div className="card">{output}</div>
    </div>
  );
}

import { useId, useState } from 'react';
import type { Analysis, TailoredDocument } from '@/api/tailoring';
import { usePasteDescription } from '@/api/tailoring';
import { Alert } from '@/components/Feedback';
import { Button } from '@/components/ui/button';
import { componentRows, errorText } from './messages';

const pct = (v: number | null | undefined) => (v === null || v === undefined ? '—' : String(Math.round(v * 100)));

/** ATS components before and after (prototype "ATS match breakdown"). */
export function AtsBreakdown({
  before,
  after,
  final,
}: {
  before: Analysis['components'];
  after: TailoredDocument['atsBreakdown'] | null;
  final: boolean;
}) {
  return (
    <div className="card">
      <h2>ATS match breakdown</h2>
      <p className="sub">
        {after
          ? final
            ? 'Before and after your changes, re-scored on the final text.'
            : 'Before and projected after your accepted changes.'
          : 'How your résumé matches this job today.'}{' '}
        Estimated; real ATS systems vary.
      </p>
      <div className="cmp" style={{ borderBottom: '1px solid var(--border)' }}>
        <span className="help">Component</span>
        <span className="num help">before</span>
        <span className="num help">{after ? 'after' : ''}</span>
      </div>
      {componentRows.map((r) => (
        <div className="cmp" key={r.key}>
          <span>{r.label}</span>
          <span className="num text-muted">{pct(before?.[r.key])}</span>
          <span className="num" style={{ color: 'var(--accent)' }}>
            {after ? pct(after[r.key]) : ''}
          </span>
        </div>
      ))}
    </div>
  );
}

/**
 * Paste the full job description (ApiDesign §4.7 `PUT /user-jobs/{id}/description`) when the listing's text is limited
 * or gone. The text is stored as typed and never used to fetch anything.
 */
export function PasteDescription({ userJobId, onSaved }: { userJobId: string; onSaved: () => void }) {
  const id = useId();
  const [text, setText] = useState('');
  const paste = usePasteDescription(userJobId);
  return (
    <div className="card">
      <h2>Paste the job description</h2>
      <p className="sub">
        We only have part of this job’s description. Paste the full text from the employer’s page so the analysis and
        your résumé changes use what the job really asks for.
      </p>
      <div className="field">
        <label className="sr-only" htmlFor={id}>
          Job description
        </label>
        <textarea
          id={id}
          className="textarea"
          style={{ minHeight: 200 }}
          maxLength={30000}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Paste the full description here"
        />
        <div className="help">{text.length.toLocaleString()} / 30,000 characters</div>
      </div>
      {paste.error && (
        <Alert tone="bad" className="mb-3">
          {errorText(paste.error)}
        </Alert>
      )}
      <Button
        variant="primary"
        disabled={text.trim().length < 50 || paste.isPending}
        onClick={() => paste.mutate(text, { onSuccess: onSaved })}
      >
        Save description
      </Button>
    </div>
  );
}

/**
 * Skill confirmation (PRD §8.3 step 2, prototype "Confirm skills"): the job asks for skills the résumé does not show.
 * "Yes" saves the skill to the confirmed skills (the only way a new skill may enter the résumé); "No" keeps it out.
 */
export function ConfirmSkills({
  skills,
  declined,
  confirming,
  onConfirm,
  onDecline,
}: {
  skills: { skill: string; likelyHas: boolean }[];
  declined: string[];
  confirming: string | null;
  onConfirm: (skill: string) => void;
  onDecline: (skill: string) => void;
}) {
  const open = skills.filter((s) => !declined.includes(s.skill));
  if (skills.length === 0) return null;
  if (open.length === 0)
    return (
      <Alert tone="info" className="mb-3.5">
        {declined.join(', ')} {declined.length === 1 ? 'stays' : 'stay'} out of your résumé. Changes that need{' '}
        {declined.length === 1 ? 'it' : 'them'} cannot be accepted.
      </Alert>
    );
  return (
    <div className="card mb-3.5">
      <h2>Confirm skills</h2>
      <p className="sub">This job asks for skills we could not find in your résumé. We only add a skill you confirm.</p>
      <div className="stack">
        {open.map((s) => (
          <div className="row between" key={s.skill}>
            <div>
              <b>{s.skill}</b>{' '}
              <span className={s.likelyHas ? 'chip info' : 'chip bad'}>
                {s.likelyHas ? 'likely from your profile' : 'missing'}
              </span>
            </div>
            <div className="row">
              <Button variant="primary" size="sm" disabled={confirming !== null} onClick={() => onConfirm(s.skill)}>
                Yes, I have used it
              </Button>
              <Button size="sm" disabled={confirming !== null} onClick={() => onDecline(s.skill)}>
                No
              </Button>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

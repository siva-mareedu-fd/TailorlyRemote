import { useState, type KeyboardEvent } from 'react';
import { isApiError } from '@/api/client';
import { useResumeProfile, useUpdateProfile, type ParsedProfile, type ProfileEdits } from '@/api/queries';
import { LoadError, Skeleton } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';

type Draft = { skills: ParsedProfile['skills']; years: string; title: string; domains: string };

function draftFrom(p: ParsedProfile): Draft {
  return {
    skills: p.skills,
    years: p.totalYears === null ? '' : String(p.totalYears),
    title: p.experience[0]?.title ?? '',
    domains: p.domains.join(', '),
  };
}

/**
 * The parsed profile with the user's corrections (PRD F2: skills add/remove, years, titles). Edits are saved as
 * `userEdited` over the parsed original; correcting the profile changes matching, not the file.
 */
export function ProfileCard({ resumeId, title = 'Parsed profile' }: { resumeId: string; title?: string }) {
  const profile = useResumeProfile(resumeId);
  const save = useUpdateProfile(resumeId);
  const say = useToast();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [newSkill, setNewSkill] = useState('');
  const [error, setError] = useState<string>();

  if (profile.isPending)
    return (
      <div className="card">
        <h2>{title}</h2>
        <div className="mt-3">
          <Skeleton />
        </div>
      </div>
    );
  if (profile.isError)
    return (
      <div className="card">
        <h2>{title}</h2>
        <div className="mt-3">
          {isApiError(profile.error, 'NOT_FOUND') ? (
            <p className="help">The profile appears once this version has been read.</p>
          ) : (
            <LoadError error={profile.error} onRetry={() => void profile.refetch()} />
          )}
        </div>
      </div>
    );

  const effective = profile.data.effective;
  const d = draft ?? draftFrom(effective);
  const dirty = draft !== null;
  const update = (patch: Partial<Draft>) => setDraft({ ...d, ...patch });

  const addSkill = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    const name = newSkill.trim();
    if (!name || d.skills.some((s) => s.name.toLowerCase() === name.toLowerCase())) return setNewSkill('');
    update({ skills: [...d.skills, { name, category: 'other' }] });
    setNewSkill('');
  };

  const onSave = async () => {
    setError(undefined);
    const years = d.years.trim() === '' ? null : Number(d.years);
    if (years !== null && (!Number.isFinite(years) || years < 0 || years > 60))
      return setError('Years of experience must be between 0 and 60.');
    const experience = effective.experience.map((r, i) => (i === 0 ? { ...r, title: d.title.trim() || r.title } : r));
    const edits: ProfileEdits = {
      ...(profile.data.userEdited ?? {}),
      skills: d.skills,
      totalYears: years,
      experience,
      domains: d.domains
        .split(',')
        .map((x) => x.trim())
        .filter(Boolean),
    };
    try {
      await save.mutateAsync(edits);
      setDraft(null);
      say('Profile saved');
    } catch (err) {
      setError(isApiError(err) ? err.userMessage : 'Could not save the profile.');
    }
  };

  return (
    <div className="card">
      <h2>{title}</h2>
      <p className="sub">Correcting this changes how we match jobs, not your file.</p>
      <h3 className="label-k mb-2.5">Skills</h3>
      <div className="profchips">
        {d.skills.length === 0 && <span className="help">No skills found.</span>}
        {d.skills.map((s) => (
          <span key={s.name} className="tagx">
            {s.name}
            <button
              type="button"
              aria-label={`Remove ${s.name}`}
              onClick={() => update({ skills: d.skills.filter((x) => x.name !== s.name) })}
            >
              ×
            </button>
          </span>
        ))}
      </div>
      <div className="row mt-2.5">
        <input
          className="input max-w-[260px]"
          placeholder="Add a skill and press Enter"
          aria-label="Add a skill"
          value={newSkill}
          maxLength={100}
          onChange={(e) => setNewSkill(e.target.value)}
          onKeyDown={addSkill}
        />
      </div>
      <div className="divider" />
      <div className="grid3 kv">
        <label>
          <div className="kk">Experience (years)</div>
          <input
            className="input font-mono"
            inputMode="decimal"
            value={d.years}
            onChange={(e) => update({ years: e.target.value })}
          />
        </label>
        <label>
          <div className="kk">Latest title</div>
          <input
            className="input"
            value={d.title}
            maxLength={200}
            onChange={(e) => update({ title: e.target.value })}
            disabled={effective.experience.length === 0}
          />
        </label>
        <label>
          <div className="kk">Domains</div>
          <input
            className="input"
            value={d.domains}
            placeholder="e.g. payments, fintech"
            onChange={(e) => update({ domains: e.target.value })}
          />
        </label>
      </div>
      {error && (
        <div className="err mt-2" role="alert">
          {error}
        </div>
      )}
      {dirty && (
        <div className="row mt-[14px]" style={{ justifyContent: 'flex-end' }}>
          <Button size="sm" onClick={() => setDraft(null)} disabled={save.isPending}>
            Discard
          </Button>
          <Button size="sm" variant="primary" onClick={onSave} disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save profile'}
          </Button>
        </div>
      )}
    </div>
  );
}

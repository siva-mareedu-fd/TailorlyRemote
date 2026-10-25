import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError } from '@/api/client';
import {
  useMeta,
  usePreferences,
  useRecordConsent,
  useResumes,
  useSavePreferences,
  type Preferences,
} from '@/api/queries';
import { VerifyEmailBanner } from '@/app/AccountNotices';
import { useSignedInMe } from '@/app/signedIn';
import { BrandMark } from '@/components/BrandMark';
import { Segmented, Setting, Switch } from '@/components/Controls';
import { Alert, PageLoading } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import {
  currencies,
  formatAnnual,
  parseAmount,
  scopes,
  type Currency,
  type Scope,
} from '@/features/preferences/options';
import { FormatFindingsList } from '@/features/resume/FormatFindings';
import { ProfileCard } from '@/features/resume/ProfileCard';
import { DropZone, UploadState } from '@/features/resume/ResumeUpload';
import { useResumeUpload } from '@/features/resume/useResumeUpload';
import { cn } from '@/lib/utils';

const STEPS = ['Résumé', 'Profile', 'Preferences', 'Schedule'];

/**
 * Onboarding wizard (design: #onboarding): 1 upload and consent, 2 review the parsed profile, 3 what to look for,
 * 4 digest schedule. Steps 3 and 4 edit the same preferences as the Preferences page and save on Continue/Finish.
 */
export function OnboardingPage() {
  const me = useSignedInMe();
  const resumes = useResumes(me.emailVerified);
  const prefs = usePreferences(me.emailVerified);
  const savePrefs = useSavePreferences();
  const [step, setStep] = useState(1);
  const [draft, setDraft] = useState<Preferences | null>(null);
  const navigate = useNavigate();
  const say = useToast();

  const [syncedToken, setSyncedToken] = useState<string | null>(null);
  // Start from the saved preferences; after each save the new token comes back with the same values.
  if (prefs.data && prefs.data.concurrencyToken !== syncedToken) {
    setSyncedToken(prefs.data.concurrencyToken);
    setDraft(prefs.data);
  }

  if (me.emailVerified && resumes.isPending) return <PageLoading />;
  const active = resumes.data?.find((r) => r.isActive && r.parseStatus === 'ready') ?? null;
  const lastStep = 4;
  const saveError = savePrefs.error instanceof ApiError ? savePrefs.error : null;
  const set = (patch: Partial<Preferences>) => {
    savePrefs.reset();
    setDraft((d) => (d ? { ...d, ...patch } : d));
  };

  const next = () => {
    if (step < 3) return setStep((s) => s + 1);
    if (!draft) return;
    savePrefs.mutate(draft, {
      onSuccess: () => {
        if (step < lastStep) return setStep((s) => s + 1);
        say(`You are all set. Your first shortlist arrives at ${draft.scheduleLocalTime}.`);
        navigate('/shortlist');
      },
    });
  };

  return (
    <div className="h-full overflow-auto">
      <div className="mx-auto max-w-[760px] px-6 pt-10 pb-20">
        <BrandMark tone="page" className="mb-7 text-[17px]" />
        <div className="stepper" aria-label="Progress">
          {STEPS.map((label, i) => {
            const n = i + 1;
            return (
              <div
                key={label}
                className={cn('step', n < step && 'done', n === step && 'now')}
                aria-current={n === step ? 'step' : undefined}
              >
                <span className="n">{n < step ? '✓' : n}</span>
                {label}
              </div>
            );
          })}
        </div>

        {step === 1 && <StepUpload activeName={active?.originalFilename ?? null} />}
        {step === 2 && <StepProfile resumeId={active!.id} />}
        {step === 3 && (draft ? <StepLooking draft={draft} set={set} /> : <PageLoading />)}
        {step === 4 &&
          (draft ? (
            <StepSchedule draft={draft} set={set} timeZone={me.timeZone} email={me.notificationEmail} />
          ) : (
            <PageLoading />
          ))}
        {saveError && step >= 3 && (
          <Alert tone="bad" className="mt-3">
            {saveError.code === 'CONCURRENT_UPDATE'
              ? 'Your preferences were changed elsewhere. Review them on the Preferences page.'
              : saveError.userMessage}
          </Alert>
        )}

        <div className="row between mt-[22px]">
          <Button onClick={() => setStep((s) => s - 1)} disabled={step === 1 || savePrefs.isPending}>
            Back
          </Button>
          <Button variant="primary" disabled={(step === 1 && !active) || savePrefs.isPending} onClick={next}>
            {savePrefs.isPending ? 'Saving…' : step < lastStep ? 'Continue' : 'Finish and see my shortlist'}
          </Button>
        </div>
      </div>
    </div>
  );
}

function StepUpload({ activeName }: { activeName: string | null }) {
  const me = useSignedInMe();
  const meta = useMeta();
  const consent = useRecordConsent();
  const upload = useResumeUpload();
  const [checked, setChecked] = useState(false);
  const current = meta.data?.consentVersion;
  const consented = !!current && me.consent?.version === current;
  const canUpload = me.emailVerified && consented && !upload.busy;

  return (
    <>
      <h1>Upload your résumé</h1>
      <p className="sub mt-1 mb-4">We read it once to understand your skills. Word (.docx) only, up to 5 MB.</p>
      {!me.emailVerified && <VerifyEmailBanner className="mb-[14px]" />}
      <DropZone onFile={upload.start} disabled={!canUpload} />
      <UploadState
        resume={upload.resume}
        fileName={upload.fileName}
        error={upload.error}
        uploading={upload.uploading}
        slow={upload.slow}
        readyText="was read. Continue to check what we found."
      />
      {!upload.resume && !upload.error && activeName && (
        <Alert tone="ok" className="mt-3">
          Parsed <b>{activeName}</b>. Continue, or upload a newer version.
        </Alert>
      )}
      <Alert tone="info" className="mt-[14px]">
        Your résumé text is processed by an AI provider (Anthropic) and is never shown to other users. You can delete it
        at any time.
      </Alert>
      {consented ? (
        <p className="help mt-3">
          You accepted the Terms and Privacy Notice (version <span className="font-mono">{current}</span>).
        </p>
      ) : (
        <div className="row mt-3">
          <label className="check">
            <input type="checkbox" checked={checked} onChange={(e) => setChecked(e.target.checked)} />
            <span>I accept the Terms and Privacy Notice.</span>
          </label>
          <Button
            size="sm"
            disabled={!checked || consent.isPending || !current}
            onClick={() => consent.mutate(current!)}
          >
            Save consent
          </Button>
        </div>
      )}
    </>
  );
}

function StepProfile({ resumeId }: { resumeId: string }) {
  const resumes = useResumes();
  const findings = resumes.data?.find((r) => r.id === resumeId)?.formatFindings;
  return (
    <>
      <h1>Check what we found</h1>
      <p className="sub mt-1 mb-4">Fix anything that looks wrong. This is only used for matching.</p>
      <ProfileCard resumeId={resumeId} title="Your profile" />
      {findings && (
        <div className="mt-[14px]">
          <FormatFindingsList findings={findings} compact />
        </div>
      )}
    </>
  );
}

type SetPreferences = (patch: Partial<Preferences>) => void;

function StepLooking({ draft, set }: { draft: Preferences; set: SetPreferences }) {
  const [roles, setRoles] = useState(draft.targetRoles.join(', '));
  const setMin = (text: string) => {
    const min = parseAmount(text);
    // Keep the saved maximum only while it is still at least the new minimum.
    const max = draft.salaryMax !== null && min !== null && draft.salaryMax < min ? null : draft.salaryMax;
    set({ salaryMin: min, salaryMax: max });
  };
  return (
    <>
      <h1>What are you looking for?</h1>
      <p className="sub mt-1 mb-4">You can change all of this later in Preferences.</p>
      <div className="card">
        <div className="field">
          <span className="lbl">Search scope</span>
          <Segmented<Scope>
            label="Search scope"
            value={draft.searchScope as Scope}
            options={scopes}
            onChange={(v) => set({ searchScope: v })}
          />
        </div>
        <div className="field">
          <label htmlFor="wr">Target roles</label>
          <input
            className="input"
            id="wr"
            value={roles}
            onChange={(e) => {
              setRoles(e.target.value);
              const list = e.target.value
                .split(',')
                .map((r) => r.trim())
                .filter(Boolean);
              set({ targetRoles: [...new Set(list)].slice(0, 25) });
            }}
          />
          <span className="help">Separate with commas.</span>
        </div>
        <div className="grid2">
          <div className="field">
            <span className="lbl">Currency</span>
            <Segmented<Currency>
              label="Currency"
              value={draft.salaryCurrency as Currency}
              options={currencies}
              onChange={(v) => set({ salaryCurrency: v })}
            />
          </div>
          <div className="field">
            <label htmlFor="wmin">Minimum salary (per year)</label>
            <input
              className="input font-mono"
              id="wmin"
              inputMode="numeric"
              value={draft.salaryMin ?? ''}
              onChange={(e) => setMin(e.target.value)}
            />
            <span className="help">{formatAnnual(draft.salaryMin, draft.salaryCurrency)}</span>
          </div>
        </div>
        <div className="field">
          <div className="row between">
            <label htmlFor="wms">Minimum match score</label>
            <span className="font-mono">{draft.minMatchScore} / 10</span>
          </div>
          <input
            type="range"
            id="wms"
            min={1}
            max={10}
            value={draft.minMatchScore}
            onChange={(e) => set({ minMatchScore: Number(e.target.value) })}
          />
        </div>
      </div>
    </>
  );
}

function StepSchedule({
  draft,
  set,
  timeZone,
  email,
}: {
  draft: Preferences;
  set: SetPreferences;
  timeZone: string;
  email: string;
}) {
  return (
    <>
      <h1>When should we send your shortlist?</h1>
      <p className="sub mt-1 mb-4">One email a day with new matches. You can turn it off any time.</p>
      <div className="card">
        <div className="grid2">
          <div className="field">
            <label htmlFor="wt">Time</label>
            <input
              className="input font-mono"
              type="time"
              id="wt"
              value={draft.scheduleLocalTime}
              onChange={(e) => set({ scheduleLocalTime: e.target.value })}
            />
          </div>
          <div className="field">
            <span className="lbl">Time zone</span>
            <div className="input">{timeZone}</div>
            <span className="help">Change it in Account.</span>
          </div>
        </div>
        <Setting title="Send a daily digest" description={`to ${email}`}>
          <Switch
            label="Send a daily digest"
            checked={draft.scheduleEnabled}
            onChange={(v) => set({ scheduleEnabled: v })}
          />
        </Setting>
      </div>
    </>
  );
}

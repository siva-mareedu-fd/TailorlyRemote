import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useMeta, useRecordConsent, useResumes } from '@/api/queries';
import { VerifyEmailBanner } from '@/app/AccountNotices';
import { useSignedInMe } from '@/app/signedIn';
import { BrandMark } from '@/components/BrandMark';
import { Alert, PageLoading } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import { FormatFindingsList } from '@/features/resume/FormatFindings';
import { ProfileCard } from '@/features/resume/ProfileCard';
import { DropZone, UploadState } from '@/features/resume/ResumeUpload';
import { useResumeUpload } from '@/features/resume/useResumeUpload';
import { cn } from '@/lib/utils';

const STEPS = ['Résumé', 'Profile', 'Preferences', 'Schedule'];

/**
 * Onboarding wizard (design: #onboarding). M1 builds steps 1 (upload, consent) and 2 (review the parsed profile);
 * preferences and schedule join in M2, so the wizard ends after step 2 for now.
 */
export function OnboardingPage() {
  const me = useSignedInMe();
  const resumes = useResumes(me.emailVerified);
  const [step, setStep] = useState(1);
  const navigate = useNavigate();
  const say = useToast();

  if (me.emailVerified && resumes.isPending) return <PageLoading />;
  const active = resumes.data?.find((r) => r.isActive && r.parseStatus === 'ready') ?? null;
  const lastStep = 2;

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

        {step === 1 ? (
          <StepUpload activeName={active?.originalFilename ?? null} />
        ) : (
          <StepProfile resumeId={active!.id} />
        )}

        <div className="row between mt-[22px]">
          <Button onClick={() => setStep((s) => s - 1)} disabled={step === 1}>
            Back
          </Button>
          <Button
            variant="primary"
            disabled={step === 1 && !active}
            onClick={() => {
              if (step < lastStep) return setStep((s) => s + 1);
              say('Your profile is ready');
              navigate('/resume');
            }}
          >
            {step < lastStep ? 'Continue' : 'Finish'}
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

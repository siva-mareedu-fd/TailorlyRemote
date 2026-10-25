import { useState } from 'react';
import { Link } from 'react-router';
import { isApiError } from '@/api/client';
import { useLogout, useMe, useMeta, useRecordConsent, useResendVerification } from '@/api/queries';
import { Alert } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';

/** Shown until the email is verified: AI features and uploads need a verified email (PRD F1). */
export function VerifyEmailBanner({ className }: { className?: string }) {
  const me = useMe();
  const resend = useResendVerification();
  const say = useToast();
  if (!me.data || me.data.emailVerified) return null;

  const onResend = async () => {
    try {
      await resend.mutateAsync();
      say('A new verification link is on its way');
    } catch (e) {
      say(isApiError(e, 'RATE_LIMITED') ? 'Too many requests. Try again in an hour.' : 'Could not send the link');
    }
  };

  return (
    <Alert
      tone="warn"
      className={className}
      action={
        <Button size="sm" onClick={onResend} disabled={resend.isPending}>
          {resend.isPending ? 'Sending…' : 'Resend link'}
        </Button>
      }
    >
      <b>Verify your email.</b> We sent a link to <span className="font-mono">{me.data.email}</span>. You need it to
      upload your résumé.
    </Alert>
  );
}

/**
 * Consent dialog (PRD F1): required when the user has not accepted the current version of the Privacy Notice, e.g.
 * after the notice changes materially. It cannot be dismissed; the alternative is signing out.
 */
export function ConsentDialog() {
  const me = useMe();
  const meta = useMeta();
  const record = useRecordConsent();
  const logout = useLogout();
  const [checked, setChecked] = useState(false);

  const current = meta.data?.consentVersion;
  const needed = !!me.data && me.data.emailVerified && !!current && me.data.consent?.version !== current;
  if (!needed) return null;

  return (
    <Dialog open>
      <DialogContent onEscapeKeyDown={(e) => e.preventDefault()} onPointerDownOutside={(e) => e.preventDefault()}>
        <DialogTitle>Please review our Privacy Notice</DialogTitle>
        <DialogDescription>
          {me.data?.consent
            ? 'The notice has changed since you last accepted it.'
            : 'We need your consent to continue.'}{' '}
          Version <span className="font-mono">{current}</span>.
        </DialogDescription>
        <Alert tone="info" className="mt-3">
          Your résumé text and the jobs you analyse are sent to an AI provider (Anthropic) to create matches and
          documents. Your résumé is never shown to other users.
        </Alert>
        <label className="check mt-3">
          <input type="checkbox" checked={checked} onChange={(e) => setChecked(e.target.checked)} />
          <span>
            I agree to the <Link to="/terms">Terms</Link> and <Link to="/privacy">Privacy Notice</Link>.
          </span>
        </label>
        {record.isError && (
          <div className="err mt-2" role="alert">
            {isApiError(record.error) ? record.error.userMessage : 'Could not save your consent.'}
          </div>
        )}
        <div className="row mt-4" style={{ justifyContent: 'flex-end' }}>
          <Button onClick={() => logout.mutate()}>Sign out</Button>
          <Button variant="primary" disabled={!checked || record.isPending} onClick={() => record.mutate(current!)}>
            Accept and continue
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

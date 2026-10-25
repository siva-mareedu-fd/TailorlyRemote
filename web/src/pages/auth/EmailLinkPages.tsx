import { CircleCheck, TriangleAlert } from 'lucide-react';
import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { isApiError } from '@/api/client';
import { useConfirmDeletion, useMe, useResetPassword, useVerifyEmail } from '@/api/queries';
import { Alert, Skeleton } from '@/components/Feedback';
import { PasswordField, PasswordMeter } from '@/components/Field';
import { Button } from '@/components/ui/button';

function useLinkParams() {
  const [params] = useSearchParams();
  const userId = params.get('userId');
  const token = params.get('token');
  return userId && token ? { userId, token } : null;
}

function Outcome({ ok, title, children }: { ok: boolean; title: string; children: ReactNode }) {
  return (
    <div>
      <div
        className={`mb-[14px] grid size-10 place-items-center rounded-alert ${ok ? 'bg-accent-soft text-accent' : 'bg-red-soft text-red'}`}
        aria-hidden="true"
      >
        {ok ? <CircleCheck size={22} /> : <TriangleAlert size={22} />}
      </div>
      <h2 className="mb-1 text-[22px] tracking-[-0.02em]">{title}</h2>
      {children}
    </div>
  );
}

/** `/verify-email?userId&token` from the verification email. */
export function VerifyEmailPage() {
  const link = useLinkParams();
  const verify = useVerifyEmail();
  const me = useMe();
  const sent = useRef(false);

  useEffect(() => {
    if (link && !sent.current) {
      sent.current = true;
      verify.mutate(link);
    }
  }, [link, verify]);

  const signedIn = !!me.data;
  if (!link || verify.isError)
    return (
      <Outcome ok={false} title="This link did not work">
        <p className="mb-[18px] text-sm text-muted">
          {verify.error && isApiError(verify.error) ? verify.error.userMessage : 'The link is incomplete.'}{' '}
          {signedIn ? 'You can ask for a new link from the banner in the app.' : 'Sign in to ask for a new link.'}
        </p>
        <Button asChild variant="primary" className="w-full py-[11px]">
          <Link to={signedIn ? '/onboarding' : '/signin'}>{signedIn ? 'Go to the app' : 'Sign in'}</Link>
        </Button>
      </Outcome>
    );
  if (!verify.isSuccess)
    return (
      <div aria-busy="true">
        <h2 className="mb-3 text-[22px] tracking-[-0.02em]">Verifying your email…</h2>
        <Skeleton lines={2} />
      </div>
    );
  return (
    <Outcome ok title="Email verified">
      <p className="mb-[18px] text-sm text-muted">Next, upload your résumé so we can find roles that fit.</p>
      <Button asChild variant="primary" className="w-full py-[11px]">
        <Link to={signedIn ? '/onboarding' : '/signin?next=%2Fonboarding'}>
          {signedIn ? 'Continue' : 'Sign in to continue'}
        </Link>
      </Button>
    </Outcome>
  );
}

/** `/reset-password?userId&token` from the reset email. */
export function ResetPasswordPage() {
  const link = useLinkParams();
  const reset = useResetPassword();
  const navigate = useNavigate();
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string>();

  if (!link)
    return (
      <Outcome ok={false} title="This link did not work">
        <p className="mb-[18px] text-sm text-muted">The link is incomplete. Ask for a new one from the sign-in page.</p>
        <Button asChild variant="primary" className="w-full py-[11px]">
          <Link to="/signin">Back to sign in</Link>
        </Button>
      </Outcome>
    );

  if (reset.isSuccess)
    return (
      <Outcome ok title="Password changed">
        <p className="mb-[18px] text-sm text-muted">Sign in with your new password.</p>
        <Button variant="primary" className="w-full py-[11px]" onClick={() => navigate('/signin')}>
          Sign in
        </Button>
      </Outcome>
    );

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (password.length < 10) return setError('Use at least 10 characters.');
    try {
      await reset.mutateAsync({ ...link, newPassword: password });
    } catch (err) {
      setError(isApiError(err) ? (err.field('newPassword') ?? err.userMessage) : 'Could not reset the password.');
    }
  };

  return (
    <>
      <h2 className="mb-1 text-[22px] tracking-[-0.02em]">Choose a new password</h2>
      <p className="mb-[18px] text-sm text-muted">The link in your email works for one hour.</p>
      <form noValidate onSubmit={submit}>
        <PasswordField
          label="New password"
          autoComplete="new-password"
          placeholder="At least 10 characters"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={error}
          hint="Use 10 or more characters. A passphrase works well."
        >
          <PasswordMeter value={password} />
        </PasswordField>
        <Button type="submit" variant="primary" className="w-full py-[11px]" disabled={reset.isPending}>
          {reset.isPending ? 'Saving…' : 'Save new password'}
        </Button>
      </form>
    </>
  );
}

/**
 * `/confirm-deletion?userId&token`. Deletion needs an explicit click here (not on page load), so link scanners that
 * open URLs in emails cannot delete an account.
 */
export function ConfirmDeletionPage() {
  const link = useLinkParams();
  const confirm = useConfirmDeletion();

  if (!link)
    return (
      <Outcome ok={false} title="This link did not work">
        <p className="mb-[18px] text-sm text-muted">The link is incomplete. Request deletion again from Account.</p>
      </Outcome>
    );
  if (confirm.isSuccess)
    return (
      <Outcome ok title="Your account is being deleted">
        <p className="mb-[18px] text-sm text-muted">
          We are removing your résumés, documents and settings now. You have been signed out. This takes at most a few
          hours.
        </p>
      </Outcome>
    );

  return (
    <>
      <h2 className="mb-1 text-[22px] tracking-[-0.02em]">Delete your account?</h2>
      <p className="mb-[18px] text-sm text-muted">
        This removes your résumés, tailored documents, tracker and preferences. It cannot be undone.
      </p>
      {confirm.isError && (
        <Alert tone="bad" className="mb-[14px]">
          {isApiError(confirm.error) ? confirm.error.userMessage : 'Could not confirm. Try again.'}
        </Alert>
      )}
      <div className="row" style={{ justifyContent: 'flex-end' }}>
        <Button asChild>
          <Link to="/">Keep my account</Link>
        </Button>
        <Button
          className="border-red bg-red font-semibold text-white hover:bg-red"
          onClick={() => confirm.mutate(link)}
          disabled={confirm.isPending}
        >
          {confirm.isPending ? 'Deleting…' : 'Delete my account'}
        </Button>
      </div>
    </>
  );
}

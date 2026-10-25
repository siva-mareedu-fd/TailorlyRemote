import { Mail } from 'lucide-react';
import { useState, type FormEvent, type KeyboardEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { isApiError } from '@/api/client';
import { useForgotPassword, useLogin, useMeta, useRegister } from '@/api/queries';
import { Alert } from '@/components/Feedback';
import { PasswordField, PasswordMeter, TextField } from '@/components/Field';
import { Button } from '@/components/ui/button';
import { safeNext } from '@/lib/navigation';
import { cn } from '@/lib/utils';

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'Asia/Kolkata';
  } catch {
    return 'Asia/Kolkata';
  }
}

type Success = { title: string; text: string };

/** Sign in / Create account card (design/tailorly-auth-B.html). `/signin` and `/signup` select the tab. */
export function AuthPage({ mode }: { mode: 'signin' | 'signup' }) {
  const navigate = useNavigate();
  const [success, setSuccess] = useState<Success | null>(null);

  if (success) {
    return (
      <div>
        <div
          className="mb-[14px] grid size-10 place-items-center rounded-alert bg-accent-soft text-accent"
          aria-hidden="true"
        >
          <Mail size={22} strokeWidth={2} />
        </div>
        <h2 className="mb-1 text-[22px] tracking-[-0.02em]">{success.title}</h2>
        <p className="mb-[18px] text-sm text-muted">{success.text}</p>
        <Button
          variant="primary"
          className="w-full py-[11px]"
          onClick={() => {
            setSuccess(null);
            navigate('/signin');
          }}
        >
          Back to sign in
        </Button>
      </div>
    );
  }

  const select = (m: 'signin' | 'signup') => navigate(m === 'signin' ? '/signin' : '/signup', { replace: true });
  const onTabKey = (e: KeyboardEvent) => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      const next = mode === 'signin' ? 'signup' : 'signin';
      select(next);
      requestAnimationFrame(() => document.getElementById(`tab-${next}`)?.focus());
    }
  };

  return (
    <>
      <div
        className="mb-[22px] grid grid-cols-2 gap-[3px] rounded-alert bg-surface-2 p-[3px]"
        role="tablist"
        aria-label="Sign in or create account"
      >
        {(['signin', 'signup'] as const).map((m) => (
          <button
            key={m}
            id={`tab-${m}`}
            type="button"
            role="tab"
            aria-selected={mode === m}
            aria-controls={`panel-${m}`}
            tabIndex={mode === m ? 0 : -1}
            onClick={() => select(m)}
            onKeyDown={onTabKey}
            className={cn(
              'cursor-pointer rounded-nav border-0 px-2.5 py-2 font-medium',
              mode === m ? 'bg-surface text-text shadow-[0_1px_2px_rgba(0,0,0,.14)]' : 'bg-transparent text-muted',
            )}
          >
            {m === 'signin' ? 'Sign in' : 'Create account'}
          </button>
        ))}
      </div>
      <div id={`panel-${mode}`} role="tabpanel" aria-labelledby={`tab-${mode}`}>
        {mode === 'signin' ? <SignInForm onSuccess={setSuccess} /> : <SignUpForm onSuccess={setSuccess} />}
      </div>
    </>
  );
}

function SignInForm({ onSuccess }: { onSuccess: (s: Success) => void }) {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const login = useLogin();
  const forgot = useForgotPassword();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<{ email?: string; password?: string; form?: string }>({});

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const next = {
      email: EMAIL_RE.test(email.trim()) ? undefined : 'Enter a valid email address.',
      password: password ? undefined : 'Enter your password.',
    };
    setErrors(next);
    if (next.email || next.password) return;
    try {
      await login.mutateAsync({ email: email.trim(), password });
      navigate(safeNext(params.get('next')), { replace: true });
    } catch (err) {
      if (isApiError(err, 'UNAUTHENTICATED')) setErrors({ password: 'Email or password is incorrect.' });
      else setErrors({ form: isApiError(err) ? err.userMessage : 'Could not sign in. Check your connection.' });
    }
  };

  const onForgot = async () => {
    const value = email.trim();
    if (!EMAIL_RE.test(value)) {
      setErrors({ email: 'Enter your email address first, then choose “Forgot password?”.' });
      return;
    }
    try {
      await forgot.mutateAsync(value);
      onSuccess({ title: 'Reset link sent', text: `If an account exists for ${value}, a reset link is on its way.` });
    } catch (err) {
      setErrors({ form: isApiError(err) ? err.userMessage : 'Could not send the link. Try again.' });
    }
  };

  return (
    <>
      <h2 className="mb-1 text-[22px] tracking-[-0.02em]">Welcome back</h2>
      <p className="mb-[18px] text-sm text-muted">Sign in to see today’s shortlist.</p>
      <form noValidate onSubmit={submit}>
        {errors.form && (
          <Alert tone="bad" className="mb-[14px]">
            {errors.form}
          </Alert>
        )}
        <TextField
          label="Email"
          type="email"
          autoComplete="email"
          placeholder="you@example.com"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          error={errors.email}
        />
        <PasswordField
          label="Password"
          autoComplete="current-password"
          placeholder="Your password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.password}
          aside={
            <button
              type="button"
              className="cursor-pointer border-0 bg-transparent p-0 text-[13px] text-accent hover:underline"
              onClick={onForgot}
              disabled={forgot.isPending}
            >
              Forgot password?
            </button>
          }
        />
        <Button type="submit" variant="primary" className="w-full py-[11px]" disabled={login.isPending}>
          {login.isPending ? 'Signing in…' : 'Sign in'}
        </Button>
      </form>
    </>
  );
}

function SignUpForm({ onSuccess }: { onSuccess: (s: Success) => void }) {
  const meta = useMeta();
  const register = useRegister();
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [invite, setInvite] = useState('');
  const [showInvite, setShowInvite] = useState(false);
  const [consent, setConsent] = useState(false);
  const [errors, setErrors] = useState<Record<string, string | undefined>>({});
  const timeZone = browserTimeZone();
  const inviteRequired = meta.data?.requireInviteCode ?? false;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const next: Record<string, string | undefined> = {
      displayName: name.trim() ? undefined : 'Tell us what to call you.',
      email: EMAIL_RE.test(email.trim()) ? undefined : 'Enter a valid email address.',
      password: password.length >= 10 ? undefined : 'Use at least 10 characters.',
      consent: consent ? undefined : 'Please accept the Terms and Privacy Notice to continue.',
      inviteCode: inviteRequired && !invite.trim() ? 'Enter your invite code.' : undefined,
    };
    setErrors(next);
    if (Object.values(next).some(Boolean) || !meta.data) return;
    try {
      await register.mutateAsync({
        email: email.trim(),
        password,
        displayName: name.trim(),
        timeZone,
        consentVersion: meta.data.consentVersion,
        inviteCode: invite.trim() || null,
      });
      onSuccess({
        title: 'Check your inbox',
        text: `We’ve sent a verification link to ${email.trim()}. Verify your email, then upload your résumé.`,
      });
    } catch (err) {
      if (!isApiError(err)) return setErrors({ form: 'Could not create the account. Check your connection.' });
      if (err.code === 'INVITE_CODE_REQUIRED' || err.code === 'INVITE_CODE_INVALID') {
        setShowInvite(true);
        return setErrors({ inviteCode: err.userMessage });
      }
      if (err.code === 'VALIDATION_FAILED')
        return setErrors({
          displayName: err.field('displayName'),
          email: err.field('email'),
          password: err.field('password'),
          form: err.field('timeZone'),
        });
      setErrors({ form: err.userMessage });
    }
  };

  return (
    <>
      <h2 className="mb-1 text-[22px] tracking-[-0.02em]">Create your account</h2>
      <p className="mb-[18px] text-sm text-muted">Upload your résumé once. Get a shortlist every evening.</p>
      <form noValidate onSubmit={submit}>
        {errors.form && (
          <Alert tone="bad" className="mb-[14px]">
            {errors.form}
          </Alert>
        )}
        <TextField
          label="Name"
          autoComplete="name"
          placeholder="Your name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          error={errors.displayName}
          maxLength={100}
        />
        <TextField
          label="Email"
          type="email"
          autoComplete="email"
          placeholder="you@example.com"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          error={errors.email}
        />
        <PasswordField
          label="Password"
          autoComplete="new-password"
          placeholder="At least 10 characters"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={errors.password}
          hint="Use 10 or more characters. A passphrase works well."
        >
          <PasswordMeter value={password} />
        </PasswordField>
        {showInvite || inviteRequired ? (
          <TextField
            label="Invite code"
            autoComplete="off"
            placeholder="Paste your code"
            value={invite}
            onChange={(e) => setInvite(e.target.value)}
            error={errors.inviteCode}
          />
        ) : (
          <div className="-mt-1 mb-3">
            <button
              type="button"
              className="cursor-pointer border-0 bg-transparent p-0 text-[13px] text-accent hover:underline"
              onClick={() => setShowInvite(true)}
            >
              Have an invite code?
            </button>
          </div>
        )}
        <label className="check mt-1.5 mb-4">
          <input
            type="checkbox"
            checked={consent}
            onChange={(e) => setConsent(e.target.checked)}
            aria-describedby={errors.consent ? 'consent-err' : undefined}
          />
          <span>
            I agree to the <Link to="/terms">Terms</Link> and <Link to="/privacy">Privacy Notice</Link>. I understand my
            résumé text is processed by an AI provider (Anthropic) to create matches and tailored documents.
          </span>
        </label>
        {errors.consent && (
          <div id="consent-err" className="err -mt-2.5 mb-3" role="alert">
            {errors.consent}
          </div>
        )}
        <Button
          type="submit"
          variant="primary"
          className="w-full py-[11px]"
          disabled={register.isPending || !meta.data}
        >
          {register.isPending ? 'Creating account…' : 'Create account'}
        </Button>
      </form>
      <div className="mt-[14px] flex items-center gap-2 border-t border-dashed border-border pt-[14px] text-[12.5px] text-muted">
        <span aria-hidden="true">◷</span>
        <span>
          Daily digest at <span className="font-mono text-text">18:00</span> ·{' '}
          <span className="font-mono text-text">{timeZone}</span>
        </span>
      </div>
    </>
  );
}

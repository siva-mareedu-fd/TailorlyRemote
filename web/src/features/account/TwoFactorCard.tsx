import { useState, type FormEvent } from 'react';
import { isApiError } from '@/api/client';
import {
  useDisableTwoFactor,
  useEnableTwoFactor,
  useNewRecoveryCodes,
  useSetupTwoFactor,
  useTwoFactor,
} from '@/api/twoFactor';
import { Alert, Skeleton } from '@/components/Feedback';
import { PasswordField, TextField } from '@/components/Field';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';

/**
 * Two-step sign-in with an authenticator app (Account page). Required for the admin console in production
 * (`Admin:RequireMfa`). The key is shown for manual entry and as an `otpauth://` link that phones hand to the
 * authenticator app; no QR image, so no extra dependency and nothing rendered from a string.
 */
export function TwoFactorCard({ isAdmin }: { isAdmin: boolean }) {
  const status = useTwoFactor();
  const [codes, setCodes] = useState<string[] | null>(null);

  return (
    <div className="card">
      <div className="row between">
        <h2>Two-step sign-in</h2>
        {status.data && (status.data.enabled ? <span className="chip ok">on</span> : <span className="chip">off</span>)}
      </div>
      <p className="sub mt-1">
        After your password, sign-in also asks for a code from an authenticator app on your phone.
        {isAdmin && ' The admin console needs it.'}
      </p>
      <div className="mt-3">
        {status.isPending ? (
          <Skeleton lines={2} />
        ) : codes ? (
          <RecoveryCodes codes={codes} onDone={() => setCodes(null)} />
        ) : status.data?.enabled ? (
          <EnabledActions left={status.data.recoveryCodesLeft} onNewCodes={setCodes} />
        ) : (
          <Setup onEnabled={setCodes} />
        )}
      </div>
    </div>
  );
}

function Setup({ onEnabled }: { onEnabled: (codes: string[]) => void }) {
  const setup = useSetupTwoFactor();
  const enable = useEnableTwoFactor();
  const [code, setCode] = useState('');
  const err = enable.error && isApiError(enable.error) ? enable.error : null;

  if (!setup.data)
    return (
      <>
        {setup.error && (
          <Alert tone="bad" className="mb-3">
            {isApiError(setup.error) ? setup.error.userMessage : 'Could not start the setup.'}
          </Alert>
        )}
        <Button onClick={() => setup.mutate()} disabled={setup.isPending}>
          {setup.isPending ? 'Preparing…' : 'Set up two-step sign-in'}
        </Button>
      </>
    );

  const submit = (e: FormEvent) => {
    e.preventDefault();
    enable.mutate(code.trim(), { onSuccess: (r) => onEnabled(r.recoveryCodes) });
  };
  return (
    <form onSubmit={submit} noValidate>
      <ol className="ml-5 list-decimal space-y-2 text-sm">
        <li>
          In your authenticator app (Google Authenticator, Microsoft Authenticator, 1Password, …) add an account with
          this key, or{' '}
          <a href={setup.data.authenticatorUri} rel="noopener noreferrer">
            open it in the app
          </a>{' '}
          on your phone:
          <div className="mt-1 font-mono text-[15px] tracking-wide select-all">{setup.data.sharedKey}</div>
        </li>
        <li>Enter the 6-digit code the app shows.</li>
      </ol>
      <div className="mt-3 max-w-[260px]">
        <TextField
          label="Code from the app"
          inputMode="numeric"
          autoComplete="one-time-code"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          error={err?.field('code') ?? (err && !err.field('code') ? err.userMessage : undefined)}
        />
      </div>
      <Button type="submit" variant="primary" disabled={enable.isPending || code.trim().length < 6}>
        {enable.isPending ? 'Checking…' : 'Turn on'}
      </Button>
    </form>
  );
}

function RecoveryCodes({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  const say = useToast();
  return (
    <>
      <Alert tone="warn" className="mb-3">
        Save these recovery codes somewhere safe. Each one signs you in once if you lose your phone. They are not shown
        again.
      </Alert>
      <ul className="grid2 mb-3 font-mono text-sm" aria-label="Recovery codes">
        {codes.map((c) => (
          <li key={c}>{c}</li>
        ))}
      </ul>
      <div className="row">
        <Button
          onClick={() =>
            void navigator.clipboard
              .writeText(codes.join('\n'))
              .then(() => say('Recovery codes copied'))
              .catch(() => say('Could not copy; select the codes instead'))
          }
        >
          Copy codes
        </Button>
        <Button variant="primary" onClick={onDone}>
          I have saved them
        </Button>
      </div>
    </>
  );
}

function EnabledActions({ left, onNewCodes }: { left: number; onNewCodes: (codes: string[]) => void }) {
  const disable = useDisableTwoFactor();
  const renew = useNewRecoveryCodes();
  const say = useToast();
  const [password, setPassword] = useState('');
  const err = [disable.error, renew.error].find((e) => e && isApiError(e));
  const fieldError = err && isApiError(err) ? (err.field('password') ?? err.userMessage) : undefined;

  return (
    <>
      <p className="help mb-3">
        {left} recovery code{left === 1 ? '' : 's'} left.{left <= 2 && ' Make new ones soon.'}
      </p>
      <div className="max-w-[320px]">
        <PasswordField
          label="Password (to make changes)"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={fieldError}
        />
      </div>
      <div className="row">
        <Button
          disabled={!password || renew.isPending}
          onClick={() =>
            renew.mutate(password, {
              onSuccess: (r) => {
                setPassword('');
                onNewCodes(r.recoveryCodes);
              },
            })
          }
        >
          New recovery codes
        </Button>
        <Button
          variant="danger"
          disabled={!password || disable.isPending}
          onClick={() =>
            disable.mutate(password, {
              onSuccess: () => {
                setPassword('');
                say('Two-step sign-in is off');
              },
            })
          }
        >
          Turn off
        </Button>
      </div>
    </>
  );
}

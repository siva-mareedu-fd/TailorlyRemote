import { useMemo, useState, type FormEvent } from 'react';
import { isApiError } from '@/api/client';
import { useChangePassword, useLogout, useRequestDeletion, useUpdateMe } from '@/api/queries';
import { useSignedInMe } from '@/app/signedIn';
import { Alert } from '@/components/Feedback';
import { PasswordField, TextField } from '@/components/Field';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';

const dateFmt = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', year: 'numeric' });

function timeZones(current: string): string[] {
  let all: string[];
  try {
    all = Intl.supportedValuesOf('timeZone');
  } catch {
    all = ['Asia/Kolkata', 'Asia/Singapore', 'Europe/London', 'America/New_York'];
  }
  return all.includes(current) ? all : [current, ...all];
}

/** Account page (design: #account): profile, password, privacy and account deletion. */
export function AccountPage() {
  const logout = useLogout();
  return (
    <div className="page narrow">
      <div className="page-title">
        <div>
          <h1>Account</h1>
          <div className="sub">Profile, password and privacy.</div>
        </div>
        <Button onClick={() => logout.mutate()} disabled={logout.isPending}>
          Sign out
        </Button>
      </div>
      <div className="stack">
        <ProfileForm />
        <PasswordForm />
        <PrivacyCard />
        <DeleteCard />
      </div>
    </div>
  );
}

function ProfileForm() {
  const me = useSignedInMe();
  const update = useUpdateMe();
  const say = useToast();
  const [name, setName] = useState(me.displayName);
  const [tz, setTz] = useState(me.timeZone);
  const [notify, setNotify] = useState(me.notificationEmail);
  const zones = useMemo(() => timeZones(me.timeZone), [me.timeZone]);
  const err = update.error && isApiError(update.error) ? update.error : null;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    update.mutate(
      { displayName: name, timeZone: tz, notificationEmail: notify },
      { onSuccess: () => say('Profile saved') },
    );
  };

  return (
    <form className="card" onSubmit={submit} noValidate>
      <h2>Profile</h2>
      <div className="grid2 mt-3">
        <TextField
          label="Name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={100}
          error={err?.field('displayName')}
        />
        <div className="field">
          <label htmlFor="acc-email">Email</label>
          <div className="row" style={{ flexWrap: 'nowrap' }}>
            <input id="acc-email" className="input" value={me.email} readOnly />
            {me.emailVerified ? (
              <span className="chip ok">verified</span>
            ) : (
              <span className="chip warn">not verified</span>
            )}
          </div>
        </div>
      </div>
      <div className="grid2">
        <div className="field">
          <label htmlFor="acc-tz">Time zone</label>
          <select id="acc-tz" className="select" value={tz} onChange={(e) => setTz(e.target.value)}>
            {zones.map((z) => (
              <option key={z}>{z}</option>
            ))}
          </select>
          <span className="help">Your daily digest time uses this time zone.</span>
          {err?.field('timeZone') && <span className="err">{err.field('timeZone')}</span>}
        </div>
        <TextField
          label="Notification email"
          type="email"
          value={notify}
          onChange={(e) => setNotify(e.target.value)}
          hint="Where digests are sent."
          error={err?.field('notificationEmail')}
        />
      </div>
      {err && Object.keys(err.fieldErrors).length === 0 && (
        <Alert tone="bad" className="mb-[14px]">
          {err.userMessage}
        </Alert>
      )}
      <Button type="submit" variant="primary" disabled={update.isPending}>
        {update.isPending ? 'Saving…' : 'Save profile'}
      </Button>
    </form>
  );
}

function PasswordForm() {
  const change = useChangePassword();
  const say = useToast();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const err = change.error && isApiError(change.error) ? change.error : null;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    change.mutate(
      { currentPassword: current, newPassword: next },
      {
        onSuccess: () => {
          setCurrent('');
          setNext('');
          say('Password changed');
        },
      },
    );
  };

  return (
    <form className="card" onSubmit={submit} noValidate>
      <h2>Password</h2>
      <div className="grid2 mt-3">
        <PasswordField
          label="Current password"
          autoComplete="current-password"
          value={current}
          onChange={(e) => setCurrent(e.target.value)}
          error={err?.field('currentPassword')}
        />
        <PasswordField
          label="New password"
          autoComplete="new-password"
          value={next}
          onChange={(e) => setNext(e.target.value)}
          error={err?.field('newPassword')}
          hint="10 or more characters."
        />
      </div>
      <Button type="submit" disabled={change.isPending || !current || next.length < 10}>
        {change.isPending ? 'Changing…' : 'Change password'}
      </Button>
    </form>
  );
}

function PrivacyCard() {
  const me = useSignedInMe();
  return (
    <div className="card">
      <h2>Privacy</h2>
      <p className="sub">
        {me.consent
          ? `You accepted the Privacy Notice (version ${me.consent.version}) on ${dateFmt.format(new Date(me.consent.at))}.`
          : 'You have not accepted the Privacy Notice yet.'}
      </p>
      <Alert tone="info">
        Your résumé text and the jobs you analyse are sent to an AI provider (Anthropic) to create matches and
        documents. Your résumé is never shown to other users.
      </Alert>
    </div>
  );
}

function DeleteCard() {
  const request = useRequestDeletion();
  const say = useToast();
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState('');

  return (
    <div className="card" style={{ borderColor: 'color-mix(in srgb, var(--red) 35%, var(--border))' }}>
      <h2>Delete account</h2>
      <p className="sub">
        Removes your résumés, tailored documents, tracker and preferences. We email a confirmation link first. This
        cannot be undone.
      </p>
      <Dialog
        open={open}
        onOpenChange={(o) => {
          setOpen(o);
          setTyped('');
        }}
      >
        <Button variant="danger" onClick={() => setOpen(true)}>
          Delete my account…
        </Button>
        <DialogContent>
          <DialogTitle>Delete your account?</DialogTitle>
          <DialogDescription>
            We will email you a confirmation link. After you confirm, your data is removed. Type{' '}
            <b className="font-mono">DELETE</b> to continue.
          </DialogDescription>
          <input
            className="input mt-3 font-mono"
            aria-label="Type DELETE"
            placeholder="DELETE"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            autoFocus
          />
          {request.isError && (
            <div className="err mt-2" role="alert">
              {isApiError(request.error) ? request.error.userMessage : 'Could not send the email.'}
            </div>
          )}
          <div className="row mt-4" style={{ justifyContent: 'flex-end' }}>
            <DialogClose asChild>
              <Button>Cancel</Button>
            </DialogClose>
            <Button
              className="border-red bg-red font-semibold text-white hover:bg-red"
              disabled={typed !== 'DELETE' || request.isPending}
              onClick={() =>
                request.mutate(undefined, {
                  onSuccess: () => {
                    setOpen(false);
                    say('Confirmation email sent. The link works for one hour.');
                  },
                })
              }
            >
              Send confirmation email
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

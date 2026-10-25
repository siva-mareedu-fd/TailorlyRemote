import { useId, useState, type InputHTMLAttributes, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

type InputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'id'>;

/** Label + input + hint + error, wired with ids and aria (prototype `.field`). */
export function TextField({
  label,
  error,
  hint,
  aside,
  className,
  ...input
}: InputProps & { label: string; error?: string; hint?: ReactNode; aside?: ReactNode }) {
  const id = useId();
  const describedBy = [hint && `${id}-hint`, error && `${id}-err`].filter(Boolean).join(' ') || undefined;
  return (
    <div className={cn('field', className)}>
      <div className="row between">
        <label htmlFor={id}>{label}</label>
        {aside}
      </div>
      <input
        id={id}
        className="input"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        {...input}
      />
      {hint && (
        <div id={`${id}-hint`} className="help">
          {hint}
        </div>
      )}
      {error && (
        <div id={`${id}-err`} className="err" role="alert">
          {error}
        </div>
      )}
    </div>
  );
}

/** Password field with the prototype's "show / hide" toggle. */
export function PasswordField({
  label,
  error,
  hint,
  aside,
  children,
  ...input
}: InputProps & { label: string; error?: string; hint?: ReactNode; aside?: ReactNode; children?: ReactNode }) {
  const id = useId();
  const [show, setShow] = useState(false);
  const describedBy = [hint && `${id}-hint`, error && `${id}-err`].filter(Boolean).join(' ') || undefined;
  return (
    <div className="field">
      <div className="row between">
        <label htmlFor={id}>{label}</label>
        {aside}
      </div>
      <div className="relative">
        <input
          id={id}
          className="input pr-14"
          type={show ? 'text' : 'password'}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          {...input}
        />
        <button
          type="button"
          className="absolute top-1/2 right-1.5 -translate-y-1/2 cursor-pointer rounded-[5px] border-0 bg-transparent px-2 py-1.5 font-mono text-[11.5px] text-muted hover:text-text"
          aria-label={show ? 'Hide password' : 'Show password'}
          onClick={() => setShow((s) => !s)}
        >
          {show ? 'hide' : 'show'}
        </button>
      </div>
      {children}
      {hint && (
        <div id={`${id}-hint`} className="help">
          {hint}
        </div>
      )}
      {error && (
        <div id={`${id}-err`} className="err" role="alert">
          {error}
        </div>
      )}
    </div>
  );
}

/** Four-segment strength meter from the sign-up prototype (length first; passphrases score well). */
export function PasswordMeter({ value }: { value: string }) {
  let s = 0;
  if (value.length >= 10) s++;
  if (value.length >= 14) s++;
  if (/[A-Z]/.test(value) && /[a-z]/.test(value)) s++;
  if ((/\d/.test(value) && /[^A-Za-z0-9]/.test(value)) || value.split(' ').length >= 3) s++;
  const level = value ? Math.max(1, s) : 0;
  const colour = level === 1 ? 'bg-red' : level === 2 ? 'bg-amber' : 'bg-accent';
  return (
    <div className="mt-0.5 grid grid-cols-4 gap-1" aria-hidden="true">
      {[1, 2, 3, 4].map((i) => (
        <i key={i} className={cn('h-1 rounded-[4px] transition-colors', i <= level ? colour : 'bg-surface-2')} />
      ))}
    </div>
  );
}

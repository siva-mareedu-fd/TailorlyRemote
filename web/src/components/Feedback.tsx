import { CircleCheck, Info, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '@/lib/utils';

export type AlertTone = 'info' | 'ok' | 'warn' | 'bad';

const icons: Record<AlertTone, ReactNode> = {
  info: <Info size={16} strokeWidth={2} aria-hidden="true" />,
  ok: <CircleCheck size={16} strokeWidth={2} aria-hidden="true" />,
  warn: <TriangleAlert size={16} strokeWidth={2} aria-hidden="true" />,
  bad: <TriangleAlert size={16} strokeWidth={2} aria-hidden="true" />,
};

/** `.alert` from the prototype: colour is always paired with an icon and text. */
export function Alert({
  tone,
  children,
  className,
  action,
}: {
  tone: AlertTone;
  children: ReactNode;
  className?: string;
  action?: ReactNode;
}) {
  return (
    <div className={cn('alert', tone, className)} role={tone === 'bad' ? 'alert' : 'status'}>
      {icons[tone]}
      <div className="min-w-0 flex-1">{children}</div>
      {action}
    </div>
  );
}

/** queued / running / ready / failed pill (every AI task shows these four states). */
export function StatusPill({ status }: { status: string }) {
  return (
    <span className={cn('status', status)}>
      <i aria-hidden="true" />
      {status}
    </span>
  );
}

export function Skeleton({ lines = 4 }: { lines?: number }) {
  const widths = ['60%', '100%', '85%', '40%', '70%', '90%'];
  return (
    <div className="stack" aria-hidden="true">
      {Array.from({ length: lines }, (_, i) => (
        <div key={i} className="skel" style={{ width: widths[i % widths.length] }} />
      ))}
    </div>
  );
}

/** Full-page loading state for a route. */
export function PageLoading({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="page narrow" aria-busy="true">
      <span className="sr-only">{label}</span>
      <div className="mb-4 h-6 w-48 skel" />
      <Skeleton lines={6} />
    </div>
  );
}

/** A query failed: say what happened and offer a retry. */
export function LoadError({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const message =
    error instanceof Error && 'userMessage' in error ? String(error.userMessage) : 'Something went wrong.';
  return (
    <Alert
      tone="bad"
      action={
        onRetry && (
          <button type="button" className="text-[13px] font-medium text-accent hover:underline" onClick={onRetry}>
            Try again
          </button>
        )
      }
    >
      <b>Could not load this.</b> {message}
    </Alert>
  );
}

export function Meter({ percent, label }: { percent: number; label: string }) {
  const tone = percent >= 100 ? 'bad' : percent >= 80 ? 'warn' : '';
  return (
    <div
      className="meter"
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={percent}
    >
      <b className={tone} style={{ width: `${Math.min(100, percent)}%` }} />
    </div>
  );
}

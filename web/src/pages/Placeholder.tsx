import { cn } from '@/lib/utils';

/** Empty page used for every route in M0. Each screen is built in the milestone named on it (PRD §15). */
export function PlaceholderPage({
  title,
  sub,
  milestone,
  narrow = false,
}: {
  title: string;
  sub?: string;
  milestone: string;
  narrow?: boolean;
}) {
  return (
    <div
      className={cn(
        'mx-auto w-full px-4 pt-[18px] pb-[72px] shell:px-8 shell:pt-[26px]',
        narrow ? 'max-w-narrow' : 'max-w-page',
      )}
    >
      <div className="mb-[18px]">
        <h1>{title}</h1>
        {sub && <p className="mt-[3px] text-[13px] text-muted">{sub}</p>}
      </div>
      <div className="empty">
        This screen is built in <span className="font-mono">{milestone}</span>.
      </div>
    </div>
  );
}

/** Content for the auth card (sign-in/sign-up), built in M1. */
export function AuthPlaceholder({ title, lead }: { title: string; lead: string }) {
  return (
    <>
      <h2 className="mb-1 text-[22px] tracking-[-0.02em]">{title}</h2>
      <p className="mb-[18px] text-sm text-muted">{lead}</p>
      <div className="empty">
        Sign-in and sign-up are built in <span className="font-mono">M1</span>.
      </div>
    </>
  );
}

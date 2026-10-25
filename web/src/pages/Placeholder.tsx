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

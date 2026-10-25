import { cn } from '@/lib/utils';

/** Logo mark + wordmark + "remote" tag, as in both prototypes. `tone="ink"` for dark panels, `"page"` on light surfaces. */
export function BrandMark({
  size = 24,
  tone = 'ink',
  className,
}: {
  size?: number;
  tone?: 'ink' | 'page';
  className?: string;
}) {
  return (
    <div className={cn('flex items-center gap-[9px] font-semibold tracking-[-0.01em]', className)}>
      <svg width={size} height={size} viewBox="0 0 28 28" aria-hidden="true">
        <rect width="28" height="28" rx="6" fill="#3ddc97" />
        <path d="M8 9h12M14 9v11" stroke="#0b1a12" strokeWidth="2.4" strokeLinecap="round" />
        <path
          d="M8 21h12"
          stroke="#0b1a12"
          strokeOpacity=".55"
          strokeWidth="1.6"
          strokeLinecap="round"
          strokeDasharray="2 3"
        />
      </svg>
      <span>Tailorly</span>
      <span
        className={cn(
          'rounded-chip px-1.5 py-px font-mono text-[10.5px] font-medium',
          tone === 'ink' ? 'border border-signal-line text-signal' : 'bg-accent-soft text-accent',
        )}
      >
        remote
      </span>
    </div>
  );
}

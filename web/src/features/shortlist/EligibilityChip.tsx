import { eligibilityLabel } from '@/lib/format';
import { cn } from '@/lib/utils';

/** Eligibility badge: green when open to India, amber with a warning sign when unclear (prototype `elChip`). */
export function EligibilityChip({ eligibility }: { eligibility: string }) {
  return (
    <span className={cn('chip', eligibility === 'unclear' ? 'warn' : eligibility === 'restricted' ? 'bad' : 'ok')}>
      {eligibilityLabel[eligibility] ?? eligibility}
    </span>
  );
}

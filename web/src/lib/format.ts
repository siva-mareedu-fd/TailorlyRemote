import type { JobSummary } from '@/api/queries';

type Salary = JobSummary['salary'];

const symbols: Record<string, string> = { USD: '$', EUR: '€', GBP: '£', INR: '₹' };

/** Compact money as in the prototype: ₹30L, ₹1.25Cr, $85k, €55.5k (same rules as the digest email). */
export function money(amount: number, currency: string | null | undefined): string {
  const symbol = currency ? (symbols[currency] ?? `${currency} `) : '';
  const trim = (n: number, digits: number) => n.toFixed(digits).replace(/\.?0+$/, '');
  if (currency === 'INR')
    return amount >= 10_000_000
      ? `${symbol}${trim(amount / 10_000_000, 2)}Cr`
      : `${symbol}${trim(amount / 100_000, 1)}L`;
  return amount >= 1000 ? `${symbol}${trim(amount / 1000, 1)}k` : `${symbol}${amount}`;
}

function range(min: number | null, max: number | null, currency: string | null | undefined): string {
  const lo = min ?? max;
  const hi = max ?? min;
  if (lo === null || hi === null) return '';
  return lo === hi ? money(lo, currency) : `${money(lo, currency)}–${money(hi, currency)}`;
}

/** The salary chip: "$60k–$90k", or "salary not listed". */
export function salaryShort(job: Pick<JobSummary, 'salary' | 'salaryLabel'>): string {
  if (job.salaryLabel === 'not_listed') return 'salary not listed';
  return range(job.salary.min, job.salary.max, job.salary.currency);
}

/** "≈ ₹57L–₹85.5L" for a non-INR salary when a rate was known; empty otherwise. */
export function approxInr(salary: Salary): string {
  const a = salary.approxInr;
  if (!a || (a.min === null && a.max === null)) return '';
  return `≈ ${range(a.min, a.max, 'INR')}`;
}

/** Chip text per eligibility; unclear carries the prototype's warning sign. */
export const eligibilityLabel: Record<string, string> = {
  worldwide: 'worldwide',
  india_eligible: 'india_eligible',
  restricted: 'restricted',
  unclear: 'unclear ⚠',
};

/** "3h ago", "1d ago", "just now" (the prototype's posted labels). */
export function ago(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return 'date not given';
  const minutes = Math.max(0, Math.round((now - new Date(iso).getTime()) / 60_000));
  if (minutes < 2) return 'just now';
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

/** "Sat 24 Oct · 18:02 IST" in the user's time zone. */
export function stamp(iso: string, timeZone: string): string {
  const parts = new Intl.DateTimeFormat('en-IN', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
    timeZone,
    timeZoneName: 'short',
  }).formatToParts(new Date(iso));
  const get = (t: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === t)?.value ?? '';
  return `${get('weekday')} ${get('day')} ${get('month')} · ${get('hour')}:${get('minute')} ${get('timeZoneName')}`;
}

/** "23:55 IST" in the user's time zone. */
export function clock(iso: string, timeZone: string): string {
  return stamp(iso, timeZone).split(' · ')[1] ?? '';
}

/** "Fri 30 Oct 2026" for a local date (YYYY-MM-DD) as stored on a digest. */
export function localDate(date: string): string {
  const [y, m, d] = date.split('-').map(Number);
  return new Intl.DateTimeFormat('en-GB', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  })
    .format(new Date(Date.UTC(y, m - 1, d)))
    .replace(',', '');
}

/** The host of a URL, for "Apply on remoteok.com". */
export function host(url: string): string {
  try {
    return new URL(url).host.replace(/^www\./, '');
  } catch {
    return 'the employer site';
  }
}

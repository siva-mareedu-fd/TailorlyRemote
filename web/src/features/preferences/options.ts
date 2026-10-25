import type { Option } from '@/components/Controls';

// Values and labels from design/tailorly-app.html (#preferences); values are the API's (ApiDesign §4.5).

export type Scope = 'india_only' | 'india_plus_global_remote' | 'global_remote_only';
export type WorkMode = 'remote' | 'hybrid' | 'onsite';
export type JobType = 'full_time' | 'contract' | 'part_time' | 'internship';
export type HiringModel = 'employee' | 'contractor';
export type Currency = 'INR' | 'USD';

export const scopes: Option<Scope>[] = [
  { value: 'india_only', label: 'India only' },
  { value: 'india_plus_global_remote', label: 'India + global remote' },
  { value: 'global_remote_only', label: 'Global remote only' },
];

export const workModes: Option<WorkMode>[] = [
  { value: 'remote', label: 'Remote' },
  { value: 'hybrid', label: 'Hybrid' },
  { value: 'onsite', label: 'On-site' },
];

export const jobTypes: Option<JobType>[] = [
  { value: 'full_time', label: 'Full-time' },
  { value: 'contract', label: 'Contract' },
  { value: 'part_time', label: 'Part-time' },
  { value: 'internship', label: 'Internship' },
];

export const hiringModels: Option<HiringModel>[] = [
  { value: 'employee', label: 'Employee' },
  { value: 'contractor', label: 'Contractor' },
];

export const currencies: Option<Currency>[] = [
  { value: 'INR', label: 'INR' },
  { value: 'USD', label: 'USD' },
];

export const overlapOptions = [
  'Any',
  'Up to 4h overlap with US East',
  'Up to 4h overlap with Europe',
  'Night shifts are fine',
];

/** "₹30L / year" or "$120k / year", as the prototype's hint under the salary fields. */
export function formatAnnual(value: number | null | undefined, currency: string): string {
  if (!value) return '';
  if (currency === 'INR') return `₹${Number((value / 100_000).toFixed(1))}L / year`;
  return `$${Math.round(value / 1000)}k / year`;
}

/** Digits only → a whole number, or null when empty. */
export function parseAmount(text: string): number | null {
  const digits = text.replace(/[^0-9]/g, '');
  return digits ? Math.min(Number(digits), 10_000_000_000) : null;
}

import { isApiError } from '@/api/client';
import type { Analysis } from '@/api/tailoring';

/** What a failed resource's error code means for the user. */
export function failureText(code: string | null | undefined): string {
  switch (code) {
    case 'BUDGET_EXCEEDED':
      return 'Your Documents budget is used up for this month. It resets on the 1st.';
    case 'AI_UNAVAILABLE':
      return 'The AI service is unavailable right now. Try again in a few minutes.';
    case 'AI_INVALID_OUTPUT':
      return 'The AI reply could not be used safely, so nothing was changed. Try again.';
    case 'JOB_DESCRIPTION_REQUIRED':
      return 'We need the full job description. Paste it below.';
    case 'ANALYSIS_REQUIRED':
      return 'Run the ATS analysis first.';
    case 'STALE_EDIT':
      return 'Your résumé changed since these changes were proposed. Generate them again.';
    case 'FORMAT_CHECK_FAILED':
      return 'The edited file did not pass our format check, so nothing was changed. Reject the change marked “formatting simplified”, or generate again.';
    case 'TIMEOUT':
      return 'This took too long and was stopped. Try again.';
    default:
      return 'Something went wrong. Try again.';
  }
}

/** The error of a failed request as one sentence (server detail first). */
export function errorText(e: unknown): string {
  if (isApiError(e)) return e.userMessage;
  return 'Something went wrong. Try again.';
}

export const componentRows: { key: keyof NonNullable<Analysis['components']>; label: string }[] = [
  { key: 'keywordCoverage', label: 'Keyword coverage' },
  { key: 'requirementsMatch', label: 'Requirements match' },
  { key: 'titleSeniority', label: 'Title and seniority' },
  { key: 'experienceRelevance', label: 'Experience relevance' },
  { key: 'formatHealth', label: 'Format health' },
];

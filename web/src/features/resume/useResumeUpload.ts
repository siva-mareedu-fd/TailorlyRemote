import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { ApiError, isApiError } from '@/api/client';
import { isInFlight, keys, useResume, useUploadResume } from '@/api/queries';

export const MAX_UPLOAD_BYTES = 5 * 1024 * 1024;

/** Client-side pre-check, mirroring the server's first checks so the user gets an instant answer. */
export function precheck(file: File): ApiError | null {
  const name = file.name.toLowerCase();
  if (name.endsWith('.pdf') || file.type === 'application/pdf')
    return new ApiError(415, {
      code: 'UNSUPPORTED_FILE_TYPE',
      detail: 'Please upload the Word (.docx) version of your résumé.',
    });
  if (!name.endsWith('.docx'))
    return new ApiError(415, {
      code: 'UNSUPPORTED_FILE_TYPE',
      detail: 'Only Word (.docx) files are supported. Save your résumé as .docx and upload that.',
    });
  if (file.size > MAX_UPLOAD_BYTES)
    return new ApiError(413, { code: 'FILE_TOO_LARGE', detail: 'The file is larger than 5 MB.' });
  return null;
}

/**
 * Upload → poll `GET /resumes/{id}` until ready or failed (ApiDesign §3). `slow` turns on after a minute so the UI can
 * say "taking longer than usual" while the background job retries.
 */
export function useResumeUpload() {
  const qc = useQueryClient();
  const upload = useUploadResume();
  const [id, setId] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string>('');
  const [startedAt, setStartedAt] = useState(() => Date.now());
  const [error, setError] = useState<ApiError | null>(null);
  const [slow, setSlow] = useState(false);
  const resume = useResume(id, startedAt);
  const status = resume.data?.parseStatus;

  useEffect(() => {
    if (!isInFlight(status)) return;
    const t = window.setTimeout(() => setSlow(true), 60_000);
    return () => window.clearTimeout(t);
  }, [status, startedAt]);

  useEffect(() => {
    if (status === 'ready' || status === 'failed') {
      void qc.invalidateQueries({ queryKey: keys.resumes, exact: true });
      void qc.invalidateQueries({ queryKey: keys.usage });
    }
  }, [status, qc]);

  const start = async (file: File) => {
    setError(null);
    setSlow(false);
    const bad = precheck(file);
    if (bad) return setError(bad);
    setFileName(file.name);
    try {
      const { resume } = await upload.mutateAsync(file);
      setStartedAt(Date.now());
      setId(resume.id);
    } catch (e) {
      setError(
        isApiError(e) ? e : new ApiError(0, { code: 'NETWORK', detail: 'Upload failed. Check your connection.' }),
      );
    }
  };

  const reset = () => {
    setId(null);
    setError(null);
    setSlow(false);
  };

  return {
    start,
    reset,
    resume: resume.data ?? null,
    fileName: resume.data?.originalFilename ?? fileName,
    uploading: upload.isPending,
    busy: upload.isPending || isInFlight(status),
    error,
    slow,
  };
}

/** Plain-language messages for parse failures (resource `errorCode`, ApiDesign §3 and §10). */
export function parseErrorMessage(code: string | null | undefined): string {
  switch (code) {
    case 'UNSUPPORTED_FILE_TYPE':
      return 'We could not read text from this file. Open it in Word, save it again as .docx and upload it.';
    case 'AI_INVALID_OUTPUT':
      return 'We could not read your résumé this time. Please upload it again.';
    case 'AI_UNAVAILABLE':
      return 'The AI service is busy. Please try again in a few minutes.';
    case 'BUDGET_EXCEEDED':
      return 'Your Documents budget for this month is used up. It resets on the 1st.';
    case 'TIMEOUT':
      return 'Reading your résumé took too long. Please try again.';
    default:
      return 'Something went wrong while reading your résumé. Please try again.';
  }
}

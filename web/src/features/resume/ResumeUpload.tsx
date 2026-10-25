import { Upload } from 'lucide-react';
import { useId, useState, type DragEvent } from 'react';
import type { ApiError } from '@/api/client';
import type { Resume } from '@/api/queries';
import { Alert, Skeleton, StatusPill } from '@/components/Feedback';
import { parseErrorMessage } from './useResumeUpload';

/** The prototype's dashed drop zone with a hidden file input; keyboard users activate the label. */
export function DropZone({ onFile, disabled }: { onFile: (f: File) => void; disabled?: boolean }) {
  const id = useId();
  const [over, setOver] = useState(false);
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    if (!disabled && e.dataTransfer.files[0]) onFile(e.dataTransfer.files[0]);
  };
  return (
    <>
      <label
        htmlFor={id}
        className="drop"
        data-over={over}
        aria-disabled={disabled}
        tabIndex={disabled ? -1 : 0}
        onKeyDown={(e) => {
          if (!disabled && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault();
            document.getElementById(id)?.click();
          }
        }}
        onDragOver={(e) => {
          e.preventDefault();
          setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={onDrop}
      >
        <Upload size={26} strokeWidth={2} aria-hidden="true" className="mx-auto" />
        <b>Choose a .docx file</b>
        <span className="help">or drag it here</span>
      </label>
      <input
        id={id}
        type="file"
        className="sr-only"
        accept=".docx,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        disabled={disabled}
        onChange={(e) => {
          const f = e.target.files?.[0];
          e.target.value = '';
          if (f) onFile(f);
        }}
      />
    </>
  );
}

/** queued / running / ready / failed for one upload (every AI task shows these states). */
export function UploadState({
  resume,
  fileName,
  error,
  uploading,
  slow,
  readyText,
}: {
  resume: Resume | null;
  fileName: string;
  error: ApiError | null;
  uploading: boolean;
  slow: boolean;
  readyText?: string;
}) {
  if (error)
    return (
      <Alert tone="bad" className="mt-3">
        <b className="font-mono">{error.code}</b> {error.userMessage}
      </Alert>
    );
  if (uploading)
    return (
      <div className="stack mt-3" aria-live="polite">
        <div className="row">
          <StatusPill status="queued" />
          <span className="help">Uploading {fileName}…</span>
        </div>
      </div>
    );
  if (!resume) return null;
  switch (resume.parseStatus) {
    case 'queued':
    case 'running':
      return (
        <div className="stack mt-3" aria-live="polite">
          <div className="row">
            <StatusPill status={resume.parseStatus} />
            <span className="help">
              {slow ? 'Taking longer than usual. You can leave this page; we keep going.' : `Reading ${fileName}…`}
            </span>
          </div>
          <Skeleton />
        </div>
      );
    case 'ready':
      return (
        <Alert tone="ok" className="mt-3">
          <b>{resume.originalFilename}</b> {readyText ?? `was parsed as version ${resume.version} and is now active.`}
        </Alert>
      );
    default:
      return (
        <Alert tone="bad" className="mt-3">
          <b className="font-mono">{resume.errorCode ?? 'FAILED'}</b> {parseErrorMessage(resume.errorCode)}
        </Alert>
      );
  }
}

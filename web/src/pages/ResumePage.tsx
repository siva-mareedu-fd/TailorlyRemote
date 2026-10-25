import { FileText, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { isApiError } from '@/api/client';
import {
  useActivateResume,
  useConfirmedSkills,
  useDeleteResume,
  useRemoveConfirmedSkill,
  useResumes,
  type Resume,
} from '@/api/queries';
import { Alert, LoadError, PageLoading, StatusPill } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button } from '@/components/ui/button';
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { FormatFindingsList } from '@/features/resume/FormatFindings';
import { ProfileCard } from '@/features/resume/ProfileCard';
import { DropZone, UploadState } from '@/features/resume/ResumeUpload';
import { useResumeUpload } from '@/features/resume/useResumeUpload';

const dateFmt = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', year: 'numeric' });
const kb = (bytes: number) => `${Math.max(1, Math.round(bytes / 1024))} KB`;

/** Résumé page (design: #resume): active version, upload, versions, format health, parsed profile, confirmed skills. */
export function ResumePage() {
  const resumes = useResumes();
  const [toDelete, setToDelete] = useState<Resume | null>(null);

  if (resumes.isPending) return <PageLoading />;
  if (resumes.isError)
    return (
      <div className="page">
        <LoadError error={resumes.error} onRetry={() => void resumes.refetch()} />
      </div>
    );

  const list = resumes.data;
  const active = list.find((r) => r.isActive) ?? null;

  return (
    <div className="page">
      <div className="page-title">
        <div>
          <h1>Résumé</h1>
          <div className="sub">One active version drives matching and tailoring. Word (.docx) only.</div>
        </div>
      </div>
      <div className="grid gap-[14px]" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))' }}>
        <div className="stack content-start">
          <ActiveCard active={active} onDelete={setToDelete} />
          <UploadCard />
          {list.length > 0 && <VersionsCard list={list} onDelete={setToDelete} />}
        </div>
        <div className="stack content-start">
          {active?.formatFindings && (
            <div className="card">
              <h2>Format health</h2>
              <p className="sub">
                These affect how automated screens read your résumé. We never restructure your layout.
              </p>
              <FormatFindingsList findings={active.formatFindings} />
            </div>
          )}
          {active && <ProfileCard key={active.id} resumeId={active.id} />}
          <ConfirmedSkillsCard />
        </div>
      </div>
      <DeleteDialog resume={toDelete} onClose={() => setToDelete(null)} />
    </div>
  );
}

function ActiveCard({ active, onDelete }: { active: Resume | null; onDelete: (r: Resume) => void }) {
  if (!active)
    return <div className="empty">No active résumé yet. Upload your Word (.docx) résumé to get started.</div>;
  return (
    <div className="card">
      <div className="row between">
        <div className="row" style={{ flexWrap: 'nowrap' }}>
          <FileText size={22} strokeWidth={2} aria-hidden="true" className="flex-none text-muted" />
          <div className="min-w-0">
            <b className="break-all">{active.originalFilename}</b>
            <div className="help">
              Version {active.version} · uploaded {dateFmt.format(new Date(active.createdAt))} · {kb(active.sizeBytes)}
            </div>
          </div>
        </div>
        <div className="row" style={{ gap: 6 }}>
          <span className="chip ok">active</span>
          <StatusPill status={active.parseStatus} />
        </div>
      </div>
      <div className="row mt-[14px]">
        <Button asChild size="sm">
          <a href={`/api/v1/resumes/${active.id}/download`} download>
            Download original
          </a>
        </Button>
        <Button size="sm" variant="danger" onClick={() => onDelete(active)}>
          <Trash2 size={14} aria-hidden="true" /> Delete
        </Button>
      </div>
    </div>
  );
}

function UploadCard() {
  const upload = useResumeUpload();
  return (
    <div className="card">
      <h2>Upload a new version</h2>
      <p className="sub">Up to 5 MB. Macros, linked files and password-protected files are rejected.</p>
      <DropZone onFile={upload.start} disabled={upload.busy} />
      <UploadState
        resume={upload.resume}
        fileName={upload.fileName}
        error={upload.error}
        uploading={upload.uploading}
        slow={upload.slow}
      />
    </div>
  );
}

function VersionsCard({ list, onDelete }: { list: Resume[]; onDelete: (r: Resume) => void }) {
  const activate = useActivateResume();
  const say = useToast();
  return (
    <div className="card">
      <h2>Versions</h2>
      <div className="tablewrap mt-2.5" style={{ border: 0 }}>
        <table>
          <thead>
            <tr>
              <th>Version</th>
              <th>Uploaded</th>
              <th>Status</th>
              <th>
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {list.map((r) => (
              <tr key={r.id}>
                <td className="font-mono">v{r.version}</td>
                <td>{dateFmt.format(new Date(r.createdAt))}</td>
                <td>{r.isActive ? <span className="chip ok">active</span> : <StatusPill status={r.parseStatus} />}</td>
                <td className="text-right whitespace-nowrap">
                  {!r.isActive && r.parseStatus === 'ready' && (
                    <Button
                      size="sm"
                      disabled={activate.isPending}
                      onClick={() =>
                        activate.mutate(r.id, {
                          onSuccess: () => say(`Version ${r.version} is now active`),
                          onError: (e) => say(isApiError(e) ? e.userMessage : 'Could not activate'),
                        })
                      }
                    >
                      Make active
                    </Button>
                  )}{' '}
                  {!r.isActive && (
                    <Button size="sm" variant="danger" onClick={() => onDelete(r)}>
                      Delete
                    </Button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function ConfirmedSkillsCard() {
  const skills = useConfirmedSkills();
  const remove = useRemoveConfirmedSkill();
  return (
    <div className="card">
      <h2>Confirmed skills</h2>
      <p className="sub">Skills you confirmed during tailoring. They may be added to tailored résumés.</p>
      {skills.isError ? (
        <LoadError error={skills.error} onRetry={() => void skills.refetch()} />
      ) : (
        <div className="profchips">
          {skills.isPending && <span className="help">Loading…</span>}
          {skills.data?.length === 0 && <span className="help">None yet.</span>}
          {skills.data?.map((s) => (
            <span key={s.id} className="tagx bg-accent-soft">
              {s.skill}
              <button type="button" aria-label={`Remove ${s.skill}`} onClick={() => remove.mutate(s.id)}>
                ×
              </button>
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function DeleteDialog({ resume, onClose }: { resume: Resume | null; onClose: () => void }) {
  const del = useDeleteResume();
  const say = useToast();
  return (
    <Dialog open={resume !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogTitle>Delete this résumé?</DialogTitle>
        <DialogDescription>
          The file, its parsed profile, and any tailored copies made from it are deleted. This cannot be undone.
          {resume?.isActive && ' Daily matching pauses until another version is active.'}
        </DialogDescription>
        {del.isError && (
          <Alert tone="bad" className="mt-3">
            {isApiError(del.error) ? del.error.userMessage : 'Could not delete.'}
          </Alert>
        )}
        <div className="row mt-4" style={{ justifyContent: 'flex-end' }}>
          <DialogClose asChild>
            <Button>Cancel</Button>
          </DialogClose>
          <Button
            className="border-red bg-red font-semibold text-white hover:bg-red"
            disabled={del.isPending}
            onClick={() =>
              resume &&
              del.mutate(resume.id, {
                onSuccess: () => {
                  say('Résumé deleted');
                  onClose();
                },
              })
            }
          >
            Delete
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

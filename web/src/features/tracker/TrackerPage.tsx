import {
  DndContext,
  PointerSensor,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import { ExternalLink, Plus } from 'lucide-react';
import { useId, useState } from 'react';
import { Link } from 'react-router';
import type { UserJob } from '@/api/tailoring';
import {
  statusLabel,
  statuses,
  useDeleteUserJob,
  useUpdateUserJob,
  useUserJobs,
  type TrackerStatus,
} from '@/api/tracker';
import { Segmented } from '@/components/Controls';
import { LoadError, PageLoading } from '@/components/Feedback';
import { useToast } from '@/components/Toast';
import { Button, buttonVariants } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { errorText } from '@/features/tailoring/messages';
import { ago } from '@/lib/format';
import { cn } from '@/lib/utils';
import { useApplyPrompt } from './useApplyPrompt';

type View = 'board' | 'list';
const VIEW_KEY = 'tracker.view';

function storedView(): View {
  try {
    return localStorage.getItem(VIEW_KEY) === 'list' ? 'list' : 'board';
  } catch {
    return 'board';
  }
}

/**
 * The application tracker (design/tailorly-app.html #tracker, PRD F11): a board with a column per status and a list
 * view. Cards move by drag and drop, or with the status dropdown on every card (keyboard and screen reader users);
 * any status can move to any other. Titles open the tailoring workspace; Details holds notes, dates, Apply and Remove.
 */
export function TrackerPage() {
  const entries = useUserJobs();
  const update = useUpdateUserJob();
  const say = useToast();
  const [view, setView] = useState<View>(storedView);
  const [open, setOpen] = useState<string | null>(null);

  const changeView = (v: View) => {
    setView(v);
    try {
      localStorage.setItem(VIEW_KEY, v);
    } catch {
      /* a convenience only */
    }
  };

  const move = (entry: UserJob, status: TrackerStatus) => {
    if (entry.status === status) return;
    update.mutate(
      { entry, status },
      { onSuccess: () => say(`Moved to ${statusLabel(status)}`), onError: (e) => say(errorText(e)) },
    );
  };

  if (entries.isPending) return <PageLoading />;
  if (entries.isError)
    return (
      <div className="page">
        <LoadError error={entries.error} onRetry={() => void entries.refetch()} />
      </div>
    );
  const all = entries.data;
  const selected = all.find((e) => e.id === open) ?? null;

  return (
    <div className="page">
      <div className="page-title">
        <div>
          <h1>Tracker</h1>
          <div className="sub">Move cards yourself. Nothing here is sent anywhere.</div>
        </div>
        <div className="row">
          <Segmented
            label="View"
            value={view}
            options={[
              { value: 'board', label: 'Board' },
              { value: 'list', label: 'List' },
            ]}
            onChange={changeView}
          />
          <Link className={buttonVariants({ variant: 'primary' })} to="/add-job">
            <Plus size={14} aria-hidden="true" /> Add job
          </Link>
        </div>
      </div>

      {all.length === 0 ? (
        <div className="empty">
          Nothing tracked yet. Save a job from your <Link to="/shortlist">shortlist</Link>, or{' '}
          <Link to="/add-job">add one you found elsewhere</Link>.
        </div>
      ) : view === 'board' ? (
        <Board entries={all} onMove={move} onOpen={setOpen} />
      ) : (
        <ListView entries={all} onMove={move} onOpen={setOpen} />
      )}

      {selected && <EntryDialog entry={selected} onClose={() => setOpen(null)} />}
    </div>
  );
}

function Board({
  entries,
  onMove,
  onOpen,
}: {
  entries: UserJob[];
  onMove: (e: UserJob, s: TrackerStatus) => void;
  onOpen: (id: string) => void;
}) {
  // A short drag threshold, so clicks on the title, the dropdown and Details still work.
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));
  const onDragEnd = (e: DragEndEvent) => {
    const entry = entries.find((x) => x.id === e.active.id);
    if (entry && e.over) onMove(entry, e.over.id as TrackerStatus);
  };
  return (
    <DndContext
      sensors={sensors}
      onDragEnd={onDragEnd}
      accessibility={{ screenReaderInstructions: { draggable: dragHelp } }}
    >
      <div className="board">
        {statuses.map(([status, label]) => (
          <Column key={status} status={status} label={label} items={entries.filter((x) => x.status === status)}>
            {(item) => <Card key={item.id} entry={item} onMove={onMove} onOpen={onOpen} />}
          </Column>
        ))}
      </div>
    </DndContext>
  );
}

const dragHelp = 'Use the status dropdown on each card to move it with the keyboard.';

function Column({
  status,
  label,
  items,
  children,
}: {
  status: TrackerStatus;
  label: string;
  items: UserJob[];
  children: (item: UserJob) => React.ReactNode;
}) {
  const { setNodeRef, isOver } = useDroppable({ id: status });
  return (
    <section ref={setNodeRef} className={cn('col', isOver && 'over')} aria-label={`${label}, ${items.length}`}>
      <h3>
        {label} <span>{items.length}</span>
      </h3>
      {items.length > 0 ? (
        items.map(children)
      ) : (
        <div className="help" style={{ padding: '10px 4px' }}>
          Nothing here yet. Drag a card here.
        </div>
      )}
    </section>
  );
}

function Card({
  entry,
  onMove,
  onOpen,
}: {
  entry: UserJob;
  onMove: (e: UserJob, s: TrackerStatus) => void;
  onOpen: (id: string) => void;
}) {
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({ id: entry.id });
  const style = transform ? { transform: `translate3d(${transform.x}px, ${transform.y}px, 0)`, zIndex: 5 } : undefined;
  return (
    <article
      ref={setNodeRef}
      style={style}
      className={cn('kcard', isDragging && 'drag')}
      {...listeners}
      {...attributes}
      tabIndex={-1}
      role="group"
      aria-roledescription="card"
      aria-label={`${entry.title} at ${entry.companyName}`}
    >
      <div className="kt">
        <Link to={`/tailor/${entry.id}`}>{entry.title}</Link>
      </div>
      <div className="text-muted" style={{ fontSize: 12.5 }}>
        {entry.companyName}
      </div>
      <Chips entry={entry} />
      {entry.notes && (
        <div className="help" style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {entry.notes}
        </div>
      )}
      <div className="kf">
        <button type="button" className="linkish" onClick={() => onOpen(entry.id)}>
          {ago(entry.updatedAt)} · Details
        </button>
        <StatusSelect entry={entry} onMove={onMove} />
      </div>
    </article>
  );
}

function Chips({ entry }: { entry: UserJob }) {
  return (
    <div className="row" style={{ gap: 6 }}>
      {entry.fitScore !== null ? (
        <span className="chip ok">fit {entry.fitScore}</span>
      ) : entry.isByo && (entry.enrichmentStatus === 'queued' || entry.enrichmentStatus === 'running') ? (
        <span className="chip">scoring…</span>
      ) : null}
      {entry.atsBefore !== null ? (
        <span className="chip info">
          ATS {entry.atsBefore}
          {entry.atsAfter !== null ? `→${entry.atsAfter}` : ''}
        </span>
      ) : (
        <span className="chip">not analysed</span>
      )}
      {entry.isByo && <span className="chip">added by you</span>}
    </div>
  );
}

function StatusSelect({
  entry,
  onMove,
  className,
}: {
  entry: UserJob;
  onMove: (e: UserJob, s: TrackerStatus) => void;
  className?: string;
}) {
  return (
    <select
      className={className}
      aria-label={`Move ${entry.title} to another status`}
      value={entry.status}
      onPointerDown={(e) => e.stopPropagation()}
      onChange={(e) => onMove(entry, e.target.value as TrackerStatus)}
    >
      {statuses.map(([value, label]) => (
        <option key={value} value={value}>
          {label}
        </option>
      ))}
    </select>
  );
}

function ListView({
  entries,
  onMove,
  onOpen,
}: {
  entries: UserJob[];
  onMove: (e: UserJob, s: TrackerStatus) => void;
  onOpen: (id: string) => void;
}) {
  return (
    <div className="tablewrap">
      <table>
        <thead>
          <tr>
            <th>Role</th>
            <th>Company</th>
            <th>Status</th>
            <th>Fit</th>
            <th>ATS</th>
            <th>Updated</th>
            <th>
              <span className="sr-only">Details</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {entries.map((e) => (
            <tr key={e.id}>
              <td>
                <Link to={`/tailor/${e.id}`}>{e.title}</Link>
              </td>
              <td>{e.companyName}</td>
              <td>
                <StatusSelect entry={e} onMove={onMove} className="select w-auto px-1.5 py-[3px]" />
              </td>
              <td className="font-mono">{e.fitScore ?? '—'}</td>
              <td className="font-mono">
                {e.atsBefore !== null ? `${e.atsBefore}${e.atsAfter !== null ? `→${e.atsAfter}` : ''}` : '—'}
              </td>
              <td className="text-muted">{ago(e.updatedAt)}</td>
              <td>
                <Button size="sm" onClick={() => onOpen(e.id)}>
                  Details
                </Button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Notes (saved with the concurrency token), status history, links, Apply with the "Did you apply?" prompt, and Remove. */
function EntryDialog({ entry, onClose }: { entry: UserJob; onClose: () => void }) {
  const notesId = useId();
  const say = useToast();
  const update = useUpdateUserJob();
  const remove = useDeleteUserJob();
  const apply = useApplyPrompt();
  const [notes, setNotes] = useState(entry.notes ?? '');
  const [confirming, setConfirming] = useState(false);
  const dirty = notes !== (entry.notes ?? '');

  return (
    <>
      <Dialog open onOpenChange={(o) => !o && onClose()}>
        <DialogContent className="max-w-[520px]">
          <DialogTitle>{entry.title}</DialogTitle>
          <DialogDescription>
            {entry.companyName} · {statusLabel(entry.status)}
            {entry.source ? ` · via ${entry.source.name}` : ''}
          </DialogDescription>

          <div className="row mt-3" style={{ gap: 8 }}>
            <Link className={buttonVariants({ variant: 'primary', size: 'sm' })} to={`/tailor/${entry.id}`}>
              Tailoring workspace
            </Link>
            {entry.applyUrl && (
              <a
                className={buttonVariants({ size: 'sm' })}
                href={entry.applyUrl}
                target="_blank"
                rel="noopener noreferrer"
                onClick={() => apply.start({ title: entry.title, companyName: entry.companyName, userJobId: entry.id })}
              >
                Apply <ExternalLink size={13} aria-hidden="true" />
              </a>
            )}
          </div>

          <div className="field mt-4">
            <label htmlFor={notesId}>Notes</label>
            <textarea
              id={notesId}
              className="textarea"
              maxLength={2000}
              style={{ minHeight: 90 }}
              value={notes}
              placeholder="Referral, recruiter's name, next steps…"
              onChange={(e) => setNotes(e.target.value)}
            />
            <div className="row">
              <Button
                size="sm"
                disabled={!dirty || update.isPending}
                onClick={() =>
                  update.mutate(
                    { entry, notes },
                    { onSuccess: () => say('Notes saved'), onError: (e) => say(errorText(e)) },
                  )
                }
              >
                Save notes
              </Button>
            </div>
          </div>

          <h3 className="k mt-4">History</h3>
          <ul className="plain">
            {[...entry.statusHistory].reverse().map((h, i) => (
              <li key={i}>
                <span>
                  {statusLabel(h.status)} <span className="text-muted">· {new Date(h.at).toLocaleString('en-IN')}</span>
                </span>
              </li>
            ))}
          </ul>

          <div className="row mt-4" style={{ justifyContent: 'space-between' }}>
            {confirming ? (
              <span className="row" style={{ gap: 8 }}>
                <span className="help">Remove with its documents and prep?</span>
                <Button
                  size="sm"
                  variant="danger"
                  disabled={remove.isPending}
                  onClick={() =>
                    remove.mutate(entry.id, {
                      onSuccess: () => {
                        say('Removed from tracker');
                        onClose();
                      },
                      onError: (e) => say(errorText(e)),
                    })
                  }
                >
                  Remove
                </Button>
                <Button size="sm" onClick={() => setConfirming(false)}>
                  Keep
                </Button>
              </span>
            ) : (
              <Button size="sm" onClick={() => setConfirming(true)}>
                Remove from tracker
              </Button>
            )}
            <Button size="sm" onClick={onClose}>
              Close
            </Button>
          </div>
        </DialogContent>
      </Dialog>
      {apply.dialog}
    </>
  );
}
